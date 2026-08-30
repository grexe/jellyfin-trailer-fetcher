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

        for (var i = 0; i < movies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMovieAsync(movies[i], config, ytDlp, stats, cancellationToken).ConfigureAwait(false);
            progress.Report((i + 1) * 100.0 / totalItems);
        }

        for (var i = 0; i < series.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSeriesAsync(series[i], config, ytDlp, stats, cancellationToken).ConfigureAwait(false);
            progress.Report((movies.Count + i + 1) * 100.0 / totalItems);
        }

        LogSummary(stats, movies.Count, series.Count, config.DryRun);

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
            return new YtDlpClient("yt-dlp", denoPath: null, config.CookiesFilePath, ffmpegDir, _logger);
        }

        var provisioner = new DependencyProvisioner(_httpClientFactory, Plugin.Instance!.DataFolderPath, _logger);
        var managedYtDlp = await provisioner.EnsureYtDlpAsync(cancellationToken).ConfigureAwait(false);
        if (managedYtDlp is null)
        {
            _logger.LogError("Could not automatically provision yt-dlp; no trailers can be fetched this run.");
            return new YtDlpClient("yt-dlp", denoPath: null, config.CookiesFilePath, ffmpegDir, _logger);
        }

        var managedDeno = await provisioner.EnsureDenoAsync(cancellationToken).ConfigureAwait(false);
        return new YtDlpClient(managedYtDlp, managedDeno, config.CookiesFilePath, ffmpegDir, _logger);
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

                    _logger.LogInformation("  > [filter] {Reason}", rejectReason);
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

        var (preferredTitle, titleVariants) = ItemMetadata.ResolveTitles(series, seriesPath);
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

                _logger.LogInformation("  > [filter] {Reason}", rejectReason);
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

    private void LogSummary(TrailerFetchStats stats, int totalMovies, int totalSeries, bool dryRun)
    {
        RunSummaryStore.Save(
            Plugin.Instance!.DataFolderPath,
            new RunSummary(
                DateTime.UtcNow,
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
                stats.SeriesSkipped));

        _logger.LogInformation(string.Empty);
        _logger.LogInformation("==========================================");
        _logger.LogInformation("           TRAILER SYNC SUMMARY           ");
        _logger.LogInformation("==========================================");
        _logger.LogInformation("  Total Movies in Library : {Count}", totalMovies);
        _logger.LogInformation("  Movies Processed        : {Count}", stats.Scanned);
        _logger.LogInformation("  Already had Trailer     : {Count}", stats.AlreadyHadTrailer);
        _logger.LogInformation(dryRun ? "  Trailers Found (Dry-Run): {Count}" : "  Trailers Downloaded     : {Count}", stats.Downloaded);
        _logger.LogInformation("  No Trailer Found        : {Count}", stats.NotFound);
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

        if (totalSeries > 0)
        {
            _logger.LogInformation("  ---------------- TV Series --------------");
            _logger.LogInformation("  Total Series in Library : {Count}", totalSeries);
            _logger.LogInformation("  Series Processed        : {Count}", stats.SeriesScanned);
            _logger.LogInformation("  Already had Trailer     : {Count}", stats.SeriesAlreadyHadTrailer);
            _logger.LogInformation(dryRun ? "  Trailers Found (Dry-Run): {Count}" : "  Trailers Downloaded     : {Count}", stats.SeriesDownloaded);
            _logger.LogInformation("  No Trailer Found        : {Count}", stats.SeriesNotFound);
            if (stats.SeriesSkipped > 0)
            {
                _logger.LogInformation("  Skipped (No Folder)     : {Count}", stats.SeriesSkipped);
            }
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
    }
}
