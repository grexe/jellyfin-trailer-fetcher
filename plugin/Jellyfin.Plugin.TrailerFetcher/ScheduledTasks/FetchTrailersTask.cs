using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.ScheduledTasks;

/// <summary>
/// Scheduled task that finds and downloads missing local movie trailers.
///
/// This is currently a skeleton: it enumerates the movie library and logs the current
/// plugin configuration to prove the plugin/task/configuration wiring is correct. The
/// actual title resolution, YouTube search/filter, yt-dlp invocation, and folder
/// migration logic (ported from the standalone Python script) lands in a follow-up.
/// </summary>
public class FetchTrailersTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<FetchTrailersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchTrailersTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FetchTrailersTask}"/> interface.</param>
    public FetchTrailersTask(ILibraryManager libraryManager, ILocalizationManager localization, ILogger<FetchTrailersTask> logger)
    {
        _libraryManager = libraryManager;
        _localization = localization;
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
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
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

        progress.Report(100);
        return Task.CompletedTask;
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
    private List<BaseItem> GetMoviesInSelectedLibraries(string[] libraryIds)
    {
        var movies = new List<BaseItem>();
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

            foreach (var movie in libraryMovies)
            {
                if (seenIds.Add(movie.Id))
                {
                    movies.Add(movie);
                }
            }
        }

        return movies;
    }

    private List<BaseItem> GetAllMovies()
    {
        _logger.LogInformation("No specific libraries selected - scanning all libraries.");
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false
        }).ToList();
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
}
