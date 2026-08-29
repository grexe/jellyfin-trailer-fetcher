using System;
using System.Collections.Generic;
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
    public string Category => "Trailer Fetcher";

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

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsVirtualItem = false
        };

        var movies = _libraryManager.GetItemList(query);
        _logger.LogInformation("Found {Count} movie(s) in the library.", movies.Count);

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
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }
}
