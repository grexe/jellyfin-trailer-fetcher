using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TrailerFetcher.Configuration;

/// <summary>
/// Controls which movies get moved into their own subfolder. Jellyfin's local-extras
/// resolver only recognizes a local trailer file when the movie has a dedicated folder
/// (see https://github.com/jellyfin/jellyfin/issues/10077) - a correctly named
/// "&lt;title&gt;-trailer" file sitting in a folder shared by other movies is silently
/// ignored.
/// </summary>
public enum MigrationMode
{
    /// <summary>
    /// Never move any movie. Local trailers will not be recognized by Jellyfin for
    /// movies that don't already have their own folder.
    /// </summary>
    Disabled,

    /// <summary>
    /// Only move movies that already have, or just got, a trailer. The rest of a flat
    /// library is left untouched.
    /// </summary>
    TrailersOnly,

    /// <summary>
    /// Move every movie encountered, regardless of trailer status.
    /// </summary>
    All
}

/// <summary>
/// Plugin configuration for Trailer Fetcher.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        RenameOriginal = false;
        MigrateToFolders = MigrationMode.Disabled;
        DryRun = false;
        TriggerLibraryScan = true;
        CookiesFilePath = string.Empty;
        MaxTrailerDurationSeconds = 300;
        LibraryIds = Array.Empty<string>();
    }

    /// <summary>
    /// Gets or sets a value indicating whether the original movie file should be
    /// renamed to match its resolved title (e.g. "Movie Name (Year).ext").
    /// </summary>
    public bool RenameOriginal { get; set; }

    /// <summary>
    /// Gets or sets which movies get moved into a dedicated "&lt;title&gt;/&lt;title&gt;.ext"
    /// subfolder so Jellyfin can actually recognize their local trailer.
    /// </summary>
    public MigrationMode MigrateToFolders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a manually triggered run should only
    /// log what it would do, without downloading, renaming, or moving anything.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Jellyfin library scan should be
    /// triggered after a run that downloaded a trailer or moved a movie, so the
    /// change is picked up immediately instead of waiting for the next scheduled scan.
    /// </summary>
    public bool TriggerLibraryScan { get; set; }

    /// <summary>
    /// Gets or sets the path to a Netscape-format cookies.txt file on the server,
    /// used by yt-dlp for authenticated/age-restricted YouTube access. Unlike the
    /// standalone script, a headless server has no browser profile to read cookies
    /// from directly, so this points at an exported cookie file instead. Leave empty
    /// to fetch without authentication.
    /// </summary>
    public string CookiesFilePath { get; set; }

    /// <summary>
    /// Gets or sets the maximum trailer duration, in seconds, accepted from a search
    /// result. Trailers are rarely longer than this; anything longer is assumed to be
    /// a full movie, compilation, or unrelated video.
    /// </summary>
    public int MaxTrailerDurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the library (VirtualFolder) ids to scan for missing trailers, by
    /// their <c>ItemId</c> as reported by <c>ILibraryManager.GetVirtualFolders()</c>.
    /// An empty array means every library is scanned.
    /// </summary>
    public string[] LibraryIds { get; set; }
}
