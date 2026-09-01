using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// Filesystem-level operations on a TV series' own top-level folder.
/// </summary>
public static class SeriesFileOperations
{
    /// <summary>
    /// Renames a series' own top-level folder to match its resolved title (e.g. a
    /// release-style "[Group] Series Name S1 - S3 [Tags]" folder becomes "Series Name
    /// (Year)"). Only the top-level folder is touched - season subfolders and
    /// everything inside them move along with it unchanged, nothing inside is
    /// renamed or restructured. Unlike a movie's own file rename, a series folder can
    /// still resolve to the right trailer via search even when left messy (Jellyfin's
    /// provider matching is forgiving), but the folder name itself is what drives
    /// Jellyfin's own UI (poster match in the collection/list view, displayed title) -
    /// this exists to fix that, independently of trailer search. Safe to call
    /// repeatedly: a folder that already matches safeTitle is left untouched.
    /// </summary>
    /// <returns>The series' folder path after the call, and whether a rename happened.</returns>
    public static (string NewSeriesPath, bool Renamed) RenameSeriesFolder(string seriesPath, string safeTitle, bool dryRun, string? libraryRoot, ILogger logger)
    {
        var parentPath = Path.GetDirectoryName(seriesPath) ?? string.Empty;
        var currentFolderName = Path.GetFileName(seriesPath);

        if (currentFolderName == safeTitle)
        {
            return (seriesPath, false);
        }

        var targetPath = Path.Combine(parentPath, safeTitle);

        if (dryRun)
        {
            logger.LogInformation("  > [DRY-RUN] Would rename series folder to: {Title}", safeTitle);
            return (seriesPath, true);
        }

        if (Directory.Exists(targetPath))
        {
            logger.LogWarning("  > Target folder {Folder} already exists. Skipping series folder rename.", safeTitle);
            return (seriesPath, false);
        }

        try
        {
            Directory.Move(seriesPath, targetPath);
            logger.LogInformation("  > Series folder renamed to: {Folder}", safeTitle);
            return (targetPath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("  > Failed to rename series folder {Folder} to {Title}: {Error}", PathDisplay.Relative(seriesPath, libraryRoot), safeTitle, e.Message);
            return (seriesPath, false);
        }
    }
}
