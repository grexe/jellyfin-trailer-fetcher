using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
    private readonly ILogger<FetchTrailersTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchTrailersTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{FetchTrailersTask}"/> interface.</param>
    public FetchTrailersTask(ILibraryManager libraryManager, ILogger<FetchTrailersTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Fetch Missing Trailers";

    /// <inheritdoc />
    public string Key => "FetchMissingTrailers";

    /// <inheritdoc />
    public string Description => "Downloads missing local movie trailers from YouTube via yt-dlp.";

    /// <inheritdoc />
    // "Library" rather than a dedicated "Trailer Fetcher" category - no need for a
    // whole extra category grouping in the Scheduled Tasks page for a single task.
    public string Category => "Library";

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

        // Diagnostic: list every library the server knows about, with the exact id
        // TopParentIds scoping below needs. Temporary - here to pin down a reported
        // "0 movies found" against a library known to contain real movie files, which
        // hasn't been reproduced locally (no live server to test against).
        var allLibraries = _libraryManager.GetVirtualFolders();
        foreach (var lib in allLibraries)
        {
            _logger.LogInformation(
                "Known library: Name={Name}, ItemId={ItemId}, CollectionType={CollectionType}",
                lib.Name,
                lib.ItemId,
                lib.CollectionType?.ToString() ?? "(none)");
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false
        };

        var libraryIds = config.LibraryIds ?? Array.Empty<string>();
        var scopedToLibraries = false;
        if (libraryIds.Length > 0)
        {
            var parsedIds = new List<Guid>();
            foreach (var id in libraryIds)
            {
                if (Guid.TryParse(id, out var guid))
                {
                    parsedIds.Add(guid);
                }
                else
                {
                    _logger.LogWarning("Configured library id '{Id}' is not a valid GUID, skipping it.", id);
                }
            }

            if (parsedIds.Count > 0)
            {
                query.TopParentIds = parsedIds.ToArray();
                scopedToLibraries = true;
                var libraryWord = parsedIds.Count == 1 ? "library" : "libraries";
                _logger.LogInformation($"Scanning {parsedIds.Count} selected {libraryWord} only: [{string.Join(", ", parsedIds)}]");
            }
        }
        else
        {
            _logger.LogInformation("No specific libraries selected - scanning all libraries.");
        }

        var movies = _libraryManager.GetItemList(query);
        _logger.LogInformation("Found {Count} movie(s) to process.", movies.Count);

        // Diagnostic: if a library scope was configured but found nothing, check
        // whether that's a scoping problem (TopParentIds not matching) or a
        // classification problem (nothing in that library is BaseItemKind.Movie at
        // all) by re-running without the library scope and without IsVirtualItem.
        if (scopedToLibraries && movies.Count == 0)
        {
            var unscopedCount = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            }).Count;
            _logger.LogInformation(
                "Diagnostic: {UnscopedCount} movie(s) found server-wide (no library scope, no IsVirtualItem filter) - " +
                "if this is also 0, the library likely isn't classified as containing Movie items; if it's > 0, " +
                "the library scope (TopParentIds) is likely not matching this library's items.",
                unscopedCount);
        }

        progress.Report(100);
        return Task.CompletedTask;
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
