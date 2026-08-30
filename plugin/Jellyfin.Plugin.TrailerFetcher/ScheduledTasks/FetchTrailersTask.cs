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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.ScheduledTasks;

/// <summary>
/// Scheduled task that finds and downloads missing local movie trailers. For each movie
/// without one: tries the official RemoteTrailers first, then a multi-stage set of
/// YouTube searches (see <see cref="TrailerSources"/>), downloads the first candidate
/// that passes duration/title filtering (see <see cref="TrailerCandidateFilter"/>) via
/// yt-dlp, and optionally renames the original file and/or migrates the movie into its
/// own folder - required for Jellyfin to recognize a local trailer at all
/// (see https://github.com/jellyfin/jellyfin/issues/10077).
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

        _logger.LogInformation("Found {Count} movie(s) to process.", movies.Count);

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

        for (var i = 0; i < movies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMovieAsync(movies[i], config, ytDlp, stats, cancellationToken).ConfigureAwait(false);
            progress.Report((i + 1) * 100.0 / movies.Count);
        }

        LogSummary(stats, movies.Count, config.DryRun);

        // Trigger a single library scan (not one per movie) so Jellyfin picks up every
        // newly downloaded trailer file, and any moved/migrated paths, in one pass.
        if (config.TriggerLibraryScan && !config.DryRun)
        {
            if (stats.Downloaded > 0 || stats.Migrated > 0)
            {
                _logger.LogInformation(
                    "Triggering a Jellyfin library scan to pick up {Downloaded} new trailer(s) and {Migrated} migrated movie(s)...",
                    stats.Downloaded,
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

        if (!MovieFileOperations.IsValidMediaFile(localPath, out var reason))
        {
            _logger.LogWarning("Skipping '{Title}': {Reason} ({Path})", rawTitle, reason, localPath);
            stats.Skipped++;
            return;
        }

        stats.Scanned++;

        var (preferredTitle, titleVariants) = MovieMetadata.ResolveTitles(movie, localPath);
        var folderPath = Path.GetDirectoryName(localPath) ?? string.Empty;

        if (stats.LastDir != folderPath)
        {
            _logger.LogInformation("*** Entering directory: {Dir}", folderPath);
            stats.LastDir = folderPath;
        }

        _logger.LogInformation("Processing movie file: {Name} ...", Path.GetFileName(localPath));

        var year = MovieMetadata.ResolveYear(movie, localPath);
        var yearStr = year is not null ? $" ({year})" : string.Empty;
        var safeTitle = TitleMatching.SanitizeFilename($"{preferredTitle}{yearStr}");

        _logger.LogInformation("  > using title '{Title}'", preferredTitle);
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
                var (newPath, renamed) = MovieFileOperations.RenameMovieFile(localPath, safeTitle, config.DryRun, _logger);
                if (renamed)
                {
                    stats.Renamed++;
                    localPath = newPath;
                    folderPath = Path.GetDirectoryName(localPath) ?? string.Empty;
                    trailerFilename = Path.Combine(folderPath, $"{safeTitle}-trailer.mp4");
                }
            }

            var sourcesToTry = TrailerSources.Build(movie, titleVariants, year);
            var logPrefix = config.DryRun ? "[DRY-RUN] " : string.Empty;

            foreach (var source in sourcesToTry)
            {
                var isSearch = source.StartsWith("ytsearch", StringComparison.Ordinal);
                _logger.LogInformation(
                    "  > {Prefix}Fetching trailer via {Kind} ({Source})...",
                    logPrefix,
                    isSearch ? "Search" : "Remote-URL",
                    source);

                if (config.DryRun)
                {
                    _logger.LogInformation("  > [DRY-RUN] Will save as: '{Name}'", Path.GetFileName(trailerFilename));
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
            var (_, moved) = MovieFileOperations.MigrateToOwnFolder(localPath, config.DryRun, [safeTitle], _logger);
            if (moved)
            {
                stats.Migrated++;
            }
        }
    }

    private void LogSummary(TrailerFetchStats stats, int totalMovies, bool dryRun)
    {
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
                _logger.LogWarning("Configured library id '{Id}' is not a valid GUID, skipping it.", id);
                continue;
            }

            var libraryItem = _libraryManager.GetItemById(guid);
            if (libraryItem is null)
            {
                _logger.LogWarning("Configured library id '{Id}' did not resolve to any item, skipping it.", guid);
                continue;
            }

            _logger.LogInformation("Scanning library '{Name}' ({Id})...", libraryItem.Name, guid);

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
    }
}
