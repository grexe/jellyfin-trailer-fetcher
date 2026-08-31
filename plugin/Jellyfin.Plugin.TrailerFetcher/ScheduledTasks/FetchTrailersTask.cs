using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TrailerFetcher.Configuration;
using Jellyfin.Plugin.TrailerFetcher.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.ScheduledTasks;

/// <summary>
/// Scheduled task that finds and downloads missing local trailers for movies and TV
/// series. For each item without one: tries the official RemoteTrailers first, then a
/// multi-stage set of YouTube searches, downloads the first candidate that passes
/// duration/title filtering via yt-dlp. Title/year resolution and source-query building
/// (<see cref="ItemMetadata"/>, <see cref="TrailerSources"/>) are shared between movies
/// and series - both are plain <see cref="BaseItem"/> lookups with no movie- or
/// series-specific behavior. What genuinely differs is kept in separate orchestration
/// methods: movies additionally support renaming the original file and/or migrating it
/// into its own folder (<see cref="MovieFileOperations"/>) - required for Jellyfin to
/// recognize a local trailer at all when movies share a flat folder
/// (see https://github.com/jellyfin/jellyfin/issues/10077) - while a series always
/// already lives in its own dedicated folder, so that step doesn't apply to it at all.
/// </summary>
public class FetchTrailersTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FetchTrailersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchTrailersTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface, used to point yt-dlp at Jellyfin's own ffmpeg.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface, used to download managed yt-dlp/deno binaries.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FetchTrailersTask}"/> interface.</param>
    public FetchTrailersTask(ILibraryManager libraryManager, ILocalizationManager localization, IMediaEncoder mediaEncoder, IHttpClientFactory httpClientFactory, ILogger<FetchTrailersTask> logger)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _mediaEncoder = mediaEncoder;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Fetch Missing Trailers";

    /// <inheritdoc />
    public string Key => "FetchMissingTrailers";

    /// <inheritdoc />
    public string Description => "Downloads missing local movie trailers from YouTube via yt-dlp.";

    /// <inheritdoc />
    // Same localized "TasksLibraryCategory" string the built-in library tasks use
    // (e.g. RefreshMediaLibraryTask) - a literal "Library" only matches in English;
    // on a non-English server it renders as its own untranslated group instead of
    // joining the real "Library" group whose label came from this same key.
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        _logger.LogInformation(
            "Trailer Fetcher run starting. RenameOriginal={RenameOriginal}, MigrateToFolders={MigrateToFolders}, " +
            "DryRun={DryRun}, TriggerLibraryScan={TriggerLibraryScan}",
            config.RenameOriginal,
            config.MigrateToFolders,
            config.DryRun,
            config.TriggerLibraryScan);

        var libraryIds = config.LibraryIds ?? Array.Empty<string>();
        var movies = libraryIds.Length > 0
            ? GetMoviesInSelectedLibraries(libraryIds)
            : GetAllMovies();
        var series = libraryIds.Length > 0
            ? GetSeriesInSelectedLibraries(libraryIds)
            : GetAllSeries();

        _logger.LogInformation("Found {MovieCount} movie(s) and {SeriesCount} series to process.", movies.Count, series.Count);

        string? ffmpegDir = null;
        try
        {
            ffmpegDir = Path.GetDirectoryName(_mediaEncoder.EncoderPath);
        }
        catch (ArgumentException)
        {
            // EncoderPath not configured yet; yt-dlp falls back to its own bundled/PATH ffmpeg.
        }

        var ytDlp = await BuildYtDlpClientAsync(config, ffmpegDir, cancellationToken).ConfigureAwait(false);
        var stats = new TrailerFetchStats();
        var totalItems = movies.Count + series.Count;
        var startedAt = DateTime.UtcNow;

        // Cancelling a run (e.g. from the dashboard), or YouTube rate-limiting the
        // session, must still leave the summary reflecting whatever was found before
        // it stopped, rather than silently leaving a stale summary from a previous run
        // on display - so both are caught here (not left to propagate straight out of
        // the loops) and the partial summary is logged/persisted either way.
        // Cancellation is then rethrown so Jellyfin's TaskManager still correctly
        // reports the run as cancelled rather than completed; a rate limit isn't an
        // admin-initiated cancellation, so that case completes normally instead (with
        // a clear ERROR-level log line and a distinct stop reason in the summary).
        OperationCanceledException? cancellation = null;
        string? stopReason = null;
        var movieIndex = 0;
        var seriesIndex = 0;
        var hasRetriedRateLimit = false;

        // There's no reliable way to know when YouTube's own rate limit actually
        // lifts (its own message only states an upper bound), so on the first hit
        // this waits once and resumes the SAME run from wherever it stopped (the
        // movie/series indices are tracked outside the try so a retry doesn't
        // restart from scratch) rather than looping/backing off indefinitely - a
        // retry that also gets rate-limited stops the run for good.
        while (true)
        {
            try
            {
                stats.MoviePhaseStarted = true;
                for (; movieIndex < movies.Count; movieIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessMovieAsync(movies[movieIndex], config, ytDlp, stats, cancellationToken).ConfigureAwait(false);
                    progress.Report((movieIndex + 1) * 100.0 / totalItems);

                    // A movie/series that completes without hitting the rate limit again
                    // is proof the limit actually lifted, not just that we got lucky once
                    // - re-arm the single retry so a *later* rate limit in this same run
                    // (a large backlog can plausibly retrigger it more than once) gets its
                    // own chance to wait-and-resume too, instead of always giving up
                    // immediately after the first retry has ever been used.
                    hasRetriedRateLimit = false;
                }

                stats.SeriesPhaseStarted = true;
                for (; seriesIndex < series.Count; seriesIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessSeriesAsync(series[seriesIndex], config, ytDlp, stats, cancellationToken).ConfigureAwait(false);
                    progress.Report((movies.Count + seriesIndex + 1) * 100.0 / totalItems);
                    hasRetriedRateLimit = false;
                }

                break;
            }
            catch (OperationCanceledException ex)
            {
                cancellation = ex;
                stopReason = "Cancelled";
                break;
            }
            catch (YouTubeRateLimitedException ex)
            {
                if (config.RetryOnRateLimit && !hasRetriedRateLimit)
                {
                    hasRetriedRateLimit = true;
                    _logger.LogWarning(
                        "{Detail} Waiting {Minutes} minute(s), then retrying the rest of this run once.",
                        ex.Message,
                        config.RateLimitRetryDelayMinutes);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(config.RateLimitRetryDelayMinutes), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException delayEx)
                    {
                        cancellation = delayEx;
                        stopReason = "Cancelled";
                        break;
                    }

                    _logger.LogInformation("Resuming after the rate-limit wait...");
                    continue;
                }

                stopReason = "YouTube rate-limited this session";
                _logger.LogError("{Detail}", ex.Message);
                break;
            }
        }

        LogSummary(stats, movies.Count, series.Count, config.DryRun, startedAt, stopReason);

        if (cancellation is not null)
        {
            _logger.LogInformation("Run cancelled - see the summary above for what was found before it stopped.");
            throw cancellation;
        }

        // Trigger a single library scan (not one per movie/series) so Jellyfin picks up
        // every newly downloaded trailer file, and any moved/migrated paths, in one pass.
        if (config.TriggerLibraryScan && !config.DryRun)
        {
            var downloaded = stats.Downloaded + stats.SeriesDownloaded;
            if (downloaded > 0 || stats.Migrated > 0)
            {
                _logger.LogInformation(
                    "Triggering a Jellyfin library scan to pick up {Downloaded} new trailer(s) and {Migrated} migrated movie(s)...",
                    downloaded,
                    stats.Migrated);
                _libraryManager.QueueLibraryScan();
            }
            else
            {
                _logger.LogInformation("No new trailers or migrations; skipping Jellyfin library scan.");
            }
        }
    }

    /// <summary>
    /// Resolves the yt-dlp and deno executables to use, downloading and managing both
    /// via <see cref="DependencyProvisioner"/> - always the plugin's own tested copies,
    /// never a system installation, so there's no server/container customization or
    /// version-mismatch support burden. A dry run never actually invokes yt-dlp (see
    /// ProcessMovieAsync), so provisioning is skipped entirely then, keeping dry-run
    /// free of side effects and instant.
    /// </summary>
    private async Task<YtDlpClient> BuildYtDlpClientAsync(PluginConfiguration config, string? ffmpegDir, CancellationToken cancellationToken)
    {
        if (config.DryRun)
        {
            return new YtDlpClient("yt-dlp", denoPath: null, config.CookiesFilePath, ffmpegDir, config.RequestDelaySeconds, _logger);
        }

        var provisioner = new DependencyProvisioner(_httpClientFactory, Plugin.Instance!.DataFolderPath, _logger);
        var managedYtDlp = await provisioner.EnsureYtDlpAsync(cancellationToken).ConfigureAwait(false);
        if (managedYtDlp is null)
        {
            _logger.LogError("Could not automatically provision yt-dlp; no trailers can be fetched this run.");
            return new YtDlpClient("yt-dlp", denoPath: null, config.CookiesFilePath, ffmpegDir, config.RequestDelaySeconds, _logger);
        }

        var managedDeno = await provisioner.EnsureDenoAsync(cancellationToken).ConfigureAwait(false);
        if (managedDeno is null)
        {
            // Not fatal - yt-dlp falls back to a bare "deno"/"node" PATH lookup, which
            // usually finds nothing on a stock container - but many videos only need
            // deno-based signature deciphering for their higher-quality formats, so
            // this can silently turn into "every download fails with 'no formats
            // available'" instead of a clear, attributable error. Worth a clear log
            // line rather than only showing up as a downstream symptom.
            _logger.LogWarning(
                "Could not provision deno; yt-dlp will fall back to a bare PATH lookup for a JS runtime, which " +
                "will likely find nothing. Videos needing signature deciphering for their formats may fail to download.");
        }

        _logger.LogInformation("Using yt-dlp: {YtDlpPath}, deno: {DenoPath}", managedYtDlp, managedDeno ?? "(not available)");
        return new YtDlpClient(managedYtDlp, managedDeno, config.CookiesFilePath, ffmpegDir, config.RequestDelaySeconds, _logger);
    }

    private async Task ProcessMovieAsync(Movie movie, PluginConfiguration config, YtDlpClient ytDlp, TrailerFetchStats stats, CancellationToken cancellationToken)
    {
        var rawTitle = string.IsNullOrEmpty(movie.Name) ? "Unknown" : movie.Name;
        var localPath = movie.Path;

        if (string.IsNullOrEmpty(localPath))
        {
            stats.Skipped++;
            return;
        }

        // The library's own root folder (e.g. "/media/Anime/Movies"), so paths in the
        // log can be shown relative to it instead of repeating the full container path
        // (mount point, library hierarchy) on every line.
        var libraryRoot = movie.GetTopParent()?.Path;

        if (!MovieFileOperations.IsValidMediaFile(localPath, out var reason))
        {
            _logger.LogWarning("Skipping {Title}: {Reason} ({Path})", rawTitle, reason, PathDisplay.Relative(localPath, libraryRoot));
            stats.Skipped++;
            return;
        }

        stats.Scanned++;

        var (preferredTitle, titleVariants) = ItemMetadata.ResolveTitles(movie, localPath);
        var folderPath = Path.GetDirectoryName(localPath) ?? string.Empty;

        if (stats.LastDir != folderPath)
        {
            _logger.LogInformation("*** Entering directory: {Dir}", PathDisplay.Relative(folderPath, libraryRoot));
            stats.LastDir = folderPath;
        }

        _logger.LogInformation("Processing movie file: {Name} ...", Path.GetFileName(localPath));

        var year = ItemMetadata.ResolveYear(movie, localPath);
        var yearStr = year is not null ? $" ({year})" : string.Empty;
        var safeTitle = TitleMatching.SanitizeFilename($"{preferredTitle}{yearStr}");

        _logger.LogInformation("  > using title {Title}", preferredTitle);
        var trailerFilename = Path.Combine(folderPath, $"{safeTitle}-trailer.mp4");

        var movieDurationSec = movie.RunTimeTicks.HasValue ? movie.RunTimeTicks.Value / 10_000_000.0 : (double?)null;

        var trailerCandidates = new[]
        {
            trailerFilename,
            Path.Combine(folderPath, $"{safeTitle}-trailer.mkv"),
            Path.Combine(folderPath, "trailer.mp4"),
            Path.Combine(folderPath, "trailer.mkv")
        };
        var alreadyHadTrailer = movie.LocalTrailers.Count > 0 || trailerCandidates.Any(File.Exists);
        var downloadSuccess = false;

        if (alreadyHadTrailer)
        {
            _logger.LogInformation("  > Trailer already exists, skipping.");
            stats.AlreadyHadTrailer++;
        }
        else
        {
            if (config.RenameOriginal)
            {
                var (newPath, renamed) = MovieFileOperations.RenameMovieFile(localPath, safeTitle, config.DryRun, libraryRoot, _logger);
                if (renamed)
                {
                    stats.Renamed++;
                    localPath = newPath;
                    folderPath = Path.GetDirectoryName(localPath) ?? string.Empty;
                    trailerFilename = Path.Combine(folderPath, $"{safeTitle}-trailer.mp4");
                }
            }

            var sourcesToTry = TrailerSources.Build(movie, titleVariants, year);

            // "[DRY-RUN] " is baked into the template text itself (per branch) rather
            // than passed as a {Prefix} value - splicing a text fragment in through a
            // structured-logging placeholder gets it quoted on its own by the logging
            // backend (the same class of bug fixed previously for a pluralization
            // suffix), which reads badly for a fragment that's sometimes empty.
            var fetchingTemplate = config.DryRun
                ? "  > [DRY-RUN] Fetching trailer via {Kind} ({Source})..."
                : "  > Fetching trailer via {Kind} ({Source})...";

            foreach (var source in sourcesToTry)
            {
                var isSearch = source.StartsWith("ytsearch", StringComparison.Ordinal);
                _logger.LogInformation(fetchingTemplate, isSearch ? "Search" : "Remote-URL", source);

                if (config.DryRun)
                {
                    _logger.LogInformation("  > [DRY-RUN] Will save as: {Name}", Path.GetFileName(trailerFilename));
                    downloadSuccess = true;
                    break;
                }

                var candidates = await ytDlp.ProbeAsync(source, cancellationToken).ConfigureAwait(false);

                YtDlpCandidate? accepted = null;
                foreach (var candidate in candidates)
                {
                    if (TrailerCandidateFilter.Accept(candidate, titleVariants, movieDurationSec, isSearch, config.MaxTrailerDurationSeconds, out var rejectReason))
                    {
                        accepted = candidate;
                        break;
                    }

                    if (config.VerboseLogging)
                    {
                        _logger.LogInformation("  > [filter] {Reason}", rejectReason);
                    }
                }

                if (accepted is not null)
                {
                    downloadSuccess = await ytDlp.DownloadAsync(accepted.WebpageUrl, trailerFilename, cancellationToken).ConfigureAwait(false);
                    if (downloadSuccess)
                    {
                        break;
                    }
                }
                else
                {
                    _logger.LogWarning("  > No suitable trailer found for source ({Source}).", source);
                }
            }

            if (downloadSuccess)
            {
                stats.Downloaded++;
            }
            else
            {
                stats.NotFound++;
            }
        }

        // Jellyfin's local-extras resolver silently ignores a correctly-named
        // "<title>-trailer" file sitting in a folder shared by multiple movies - it only
        // recognizes one when the movie has its own folder
        // (https://github.com/jellyfin/jellyfin/issues/10077). "All" migrates every
        // movie; "TrailersOnly" only migrates movies that actually have a trailer
        // (pre-existing or just downloaded), leaving the rest of a flat library untouched.
        var shouldMigrate = config.MigrateToFolders == MigrationMode.All ||
                             (config.MigrateToFolders == MigrationMode.TrailersOnly && (alreadyHadTrailer || downloadSuccess));
        if (shouldMigrate)
        {
            var (_, moved) = MovieFileOperations.MigrateToOwnFolder(localPath, config.DryRun, [safeTitle], libraryRoot, _logger);
            if (moved)
            {
                stats.Migrated++;
            }
        }
    }

    /// <summary>
    /// Processes a single TV series: tries its official RemoteTrailers, then a
    /// multi-stage YouTube search (see <see cref="TrailerSources"/>), same duration/
    /// title filtering as movies. Title/year resolution and source-query building are
    /// shared with ProcessMovieAsync via <see cref="ItemMetadata"/>/
    /// <see cref="TrailerSources"/> (both are plain BaseItem lookups, no movie- or
    /// series-specific behavior); kept as a separate method rather than a shared "item"
    /// loop because the actual steps genuinely differ - no rename/migrate here, since a
    /// series always already lives in its own dedicated folder, so the "own folder"
    /// problem that drives that logic for movies (jellyfin/jellyfin#10077) doesn't
    /// apply, and validity is a folder-exists check rather than
    /// <see cref="MovieFileOperations.IsValidMediaFile"/>.
    /// </summary>
    private async Task ProcessSeriesAsync(Series series, PluginConfiguration config, YtDlpClient ytDlp, TrailerFetchStats stats, CancellationToken cancellationToken)
    {
        var rawTitle = string.IsNullOrEmpty(series.Name) ? "Unknown" : series.Name;
        var seriesPath = series.Path;

        if (string.IsNullOrEmpty(seriesPath) || !Directory.Exists(seriesPath))
        {
            _logger.LogWarning("Skipping series {Title}: folder not found ({Path})", rawTitle, seriesPath);
            stats.SeriesSkipped++;
            return;
        }

        var libraryRoot = series.GetTopParent()?.Path;
        stats.SeriesScanned++;

        _logger.LogInformation("*** Processing series: {Name}", PathDisplay.Relative(seriesPath, libraryRoot));

        var (resolvedTitle, resolvedVariants) = ItemMetadata.ResolveTitles(series, seriesPath);

        // Season-range noise ("S1", "S1 - S5") is TV-specific - see
        // SeriesTitleCleanup for why this is a separate step rather than something
        // ItemMetadata/TitleMatching (shared with movies) needs to know about.
        var preferredTitle = SeriesTitleCleanup.StripSeasonRange(resolvedTitle);
        var titleVariants = resolvedVariants
            .Select(SeriesTitleCleanup.StripSeasonRange)
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        var year = ItemMetadata.ResolveYear(series, seriesPath);
        var yearStr = year is not null ? $" ({year})" : string.Empty;
        var safeTitle = TitleMatching.SanitizeFilename($"{preferredTitle}{yearStr}");

        _logger.LogInformation("  > using title {Title}", preferredTitle);
        var trailerFilename = Path.Combine(seriesPath, $"{safeTitle}-trailer.mp4");

        var trailerCandidates = new[]
        {
            trailerFilename,
            Path.Combine(seriesPath, $"{safeTitle}-trailer.mkv"),
            Path.Combine(seriesPath, "trailer.mp4"),
            Path.Combine(seriesPath, "trailer.mkv")
        };
        var alreadyHadTrailer = series.LocalTrailers.Count > 0 || trailerCandidates.Any(File.Exists);

        if (alreadyHadTrailer)
        {
            _logger.LogInformation("  > Trailer already exists, skipping.");
            stats.SeriesAlreadyHadTrailer++;
            return;
        }

        var sourcesToTry = TrailerSources.Build(series, titleVariants, year);
        var fetchingTemplate = config.DryRun
            ? "  > [DRY-RUN] Fetching trailer via {Kind} ({Source})..."
            : "  > Fetching trailer via {Kind} ({Source})...";
        var downloadSuccess = false;

        foreach (var source in sourcesToTry)
        {
            var isSearch = source.StartsWith("ytsearch", StringComparison.Ordinal);
            _logger.LogInformation(fetchingTemplate, isSearch ? "Search" : "Remote-URL", source);

            if (config.DryRun)
            {
                _logger.LogInformation("  > [DRY-RUN] Will save as: {Name}", Path.GetFileName(trailerFilename));
                downloadSuccess = true;
                break;
            }

            var candidates = await ytDlp.ProbeAsync(source, cancellationToken).ConfigureAwait(false);

            YtDlpCandidate? accepted = null;
            foreach (var candidate in candidates)
            {
                // No single "runtime" to compare a series trailer against, unlike a
                // movie - only the universal duration cap applies.
                if (TrailerCandidateFilter.Accept(candidate, titleVariants, movieDurationSeconds: null, isSearch, config.MaxTrailerDurationSeconds, out var rejectReason))
                {
                    accepted = candidate;
                    break;
                }

                if (config.VerboseLogging)
                {
                    _logger.LogInformation("  > [filter] {Reason}", rejectReason);
                }
            }

            if (accepted is not null)
            {
                downloadSuccess = await ytDlp.DownloadAsync(accepted.WebpageUrl, trailerFilename, cancellationToken).ConfigureAwait(false);
                if (downloadSuccess)
                {
                    break;
                }
            }
            else
            {
                _logger.LogWarning("  > No suitable trailer found for source ({Source}).", source);
            }
        }

        if (downloadSuccess)
        {
            stats.SeriesDownloaded++;
        }
        else
        {
            stats.SeriesNotFound++;
        }
    }

    private void LogSummary(TrailerFetchStats stats, int totalMovies, int totalSeries, bool dryRun, DateTime startedAt, string? stopReason)
    {
        var completedAt = DateTime.UtcNow;
        RunSummaryStore.Save(
            Plugin.Instance!.DataFolderPath,
            new RunSummary(
                completedAt,
                (completedAt - startedAt).TotalSeconds,
                stopReason,
                dryRun,
                totalMovies,
                stats.Scanned,
                stats.AlreadyHadTrailer,
                stats.Downloaded,
                stats.NotFound,
                stats.Skipped,
                stats.Renamed,
                stats.Migrated,
                totalSeries,
                stats.SeriesScanned,
                stats.SeriesAlreadyHadTrailer,
                stats.SeriesDownloaded,
                stats.SeriesNotFound,
                stats.SeriesSkipped,
                stats.MoviePhaseStarted,
                stats.SeriesPhaseStarted));

        // "0" and "never got to it" look identical as a bare count otherwise - e.g. a
        // run that got rate-limited partway through movies, with series never
        // touched, would show "Series Processed: 0" exactly like a run that reached
        // every series and matched none.
        static object Fmt(bool phaseStarted, int count) => phaseStarted ? count : "n/a";

        _logger.LogInformation(string.Empty);
        _logger.LogInformation("==========================================");
        if (stopReason is not null)
        {
            _logger.LogInformation("     TRAILER SYNC SUMMARY [{StopReason}]   ", stopReason);
        }
        else
        {
            _logger.LogInformation("           TRAILER SYNC SUMMARY           ");
        }

        _logger.LogInformation("==========================================");

        // Only the libraries actually in scope for this run get a section - a run
        // scoped to series-only libraries showing "Total Movies in Library: 0" reads
        // as if something's wrong rather than as "no movies were in scope".
        if (totalMovies > 0)
        {
            _logger.LogInformation("  Total Movies in Library : {Count}", totalMovies);
            _logger.LogInformation("  Movies Processed        : {Count}", Fmt(stats.MoviePhaseStarted, stats.Scanned));
            _logger.LogInformation("  Already had Trailer     : {Count}", Fmt(stats.MoviePhaseStarted, stats.AlreadyHadTrailer));
            _logger.LogInformation(dryRun ? "  Trailers Found (Dry-Run): {Count}" : "  Trailers Downloaded     : {Count}", Fmt(stats.MoviePhaseStarted, stats.Downloaded));
            _logger.LogInformation("  No Trailer Found        : {Count}", Fmt(stats.MoviePhaseStarted, stats.NotFound));
            if (stats.Skipped > 0)
            {
                _logger.LogInformation("  Skipped (Unreachable)   : {Count}", stats.Skipped);
            }

            if (stats.Renamed > 0)
            {
                _logger.LogInformation("  Original Files Renamed  : {Count}", stats.Renamed);
            }

            if (stats.Migrated > 0)
            {
                _logger.LogInformation("  Migrated to Own Folder  : {Count}", stats.Migrated);
            }
        }

        if (totalSeries > 0)
        {
            if (totalMovies > 0)
            {
                _logger.LogInformation("  ---------------- TV Series --------------");
            }

            _logger.LogInformation("  Total Series in Library : {Count}", totalSeries);
            _logger.LogInformation("  Series Processed        : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesScanned));
            _logger.LogInformation("  Already had Trailer     : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesAlreadyHadTrailer));
            _logger.LogInformation(dryRun ? "  Trailers Found (Dry-Run): {Count}" : "  Trailers Downloaded     : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesDownloaded));
            _logger.LogInformation("  No Trailer Found        : {Count}", Fmt(stats.SeriesPhaseStarted, stats.SeriesNotFound));
            if (stats.SeriesSkipped > 0)
            {
                _logger.LogInformation("  Skipped (No Folder)     : {Count}", stats.SeriesSkipped);
            }
        }

        if (totalMovies == 0 && totalSeries == 0)
        {
            _logger.LogInformation("  Nothing in scope for this run.");
        }

        _logger.LogInformation("==========================================");
    }

    /// <summary>
    /// Queries movies scoped to the configured libraries. Each library is resolved to
    /// its actual <see cref="BaseItem"/> via <see cref="ILibraryManager.GetItemById(Guid)"/>
    /// and queried individually via <see cref="InternalItemsQuery.Parent"/>, rather than
    /// hand-building <see cref="InternalItemsQuery.TopParentIds"/> from
    /// <c>VirtualFolderInfo.ItemId</c> directly - that id does not reliably match what
    /// TopParentIds-based filtering expects (confirmed: a query scoped this way
    /// returned 0 results against a library server-side reporting had 1026+ movies
    /// total), and TopParentIds is normally computed internally by LibraryManager from
    /// a resolved parent item, not meant to be built by callers from a raw id string.
    /// </summary>
    private List<Movie> GetMoviesInSelectedLibraries(string[] libraryIds)
    {
        var movies = new List<Movie>();
        var seenIds = new HashSet<Guid>();

        foreach (var id in libraryIds)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                _logger.LogWarning("Configured library id {Id} is not a valid GUID, skipping it.", id);
                continue;
            }

            var libraryItem = _libraryManager.GetItemById(guid);
            if (libraryItem is null)
            {
                _logger.LogWarning("Configured library id {Id} did not resolve to any item, skipping it.", guid);
                continue;
            }

            _logger.LogInformation("Scanning library {Name}...", libraryItem.Name);

            var libraryMovies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false,
                Parent = libraryItem
            });

            foreach (var movie in libraryMovies.OfType<Movie>())
            {
                if (seenIds.Add(movie.Id))
                {
                    movies.Add(movie);
                }
            }
        }

        return movies;
    }

    private List<Movie> GetAllMovies()
    {
        _logger.LogInformation("No specific libraries selected - scanning all libraries.");
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Movie>().ToList();
    }

    /// <summary>
    /// Queries TV series scoped to the configured libraries. Deliberately a separate,
    /// parallel query from <see cref="GetMoviesInSelectedLibraries"/> rather than one
    /// combined query split by type afterward, keeping the movie and series paths fully
    /// independent end to end.
    /// </summary>
    private List<Series> GetSeriesInSelectedLibraries(string[] libraryIds)
    {
        var series = new List<Series>();
        var seenIds = new HashSet<Guid>();

        foreach (var id in libraryIds)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                // Already warned about in GetMoviesInSelectedLibraries for the same run.
                continue;
            }

            var libraryItem = _libraryManager.GetItemById(guid);
            if (libraryItem is null)
            {
                continue;
            }

            var librarySeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
                Parent = libraryItem
            });

            foreach (var s in librarySeries.OfType<Series>())
            {
                if (seenIds.Add(s.Id))
                {
                    series.Add(s);
                }
            }
        }

        return series;
    }

    private List<Series> GetAllSeries()
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Series>().ToList();
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }

    private sealed class TrailerFetchStats
    {
        public string? LastDir { get; set; }

        public int Scanned { get; set; }

        public int AlreadyHadTrailer { get; set; }

        public int Downloaded { get; set; }

        public int NotFound { get; set; }

        public int Skipped { get; set; }

        public int Renamed { get; set; }

        public int Migrated { get; set; }

        public int SeriesScanned { get; set; }

        public int SeriesAlreadyHadTrailer { get; set; }

        public int SeriesDownloaded { get; set; }

        public int SeriesNotFound { get; set; }

        public int SeriesSkipped { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the movie loop was ever entered
        /// this run - false only if the run stopped (cancelled/rate-limited) before
        /// reaching it at all, which the movie counts above can't distinguish from
        /// "genuinely processed zero" on their own.
        /// </summary>
        public bool MoviePhaseStarted { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the series loop was ever entered
        /// this run - false if the run stopped while still working through movies
        /// (movies are always processed first), which otherwise looks identical to
        /// "128 series in scope, 0 matched" in the summary.
        /// </summary>
        public bool SeriesPhaseStarted { get; set; }
    }
}
