using System;
using System.IO;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// Renders a filesystem path relative to a movie's library root for logging, so log
/// output doesn't repeat the full absolute path (container mount point, library
/// hierarchy, etc.) on every line - just the part that's actually specific to the movie.
/// </summary>
public static class PathDisplay
{
    /// <summary>Returns <paramref name="fullPath"/> relative to <paramref name="libraryRoot"/>, or unchanged if that isn't possible.</summary>
    public static string Relative(string fullPath, string? libraryRoot)
    {
        if (string.IsNullOrEmpty(libraryRoot) || string.IsNullOrEmpty(fullPath))
        {
            return fullPath;
        }

        try
        {
            var relative = Path.GetRelativePath(libraryRoot, fullPath);

            // GetRelativePath returns an unrelated-looking result (starting with "..")
            // when fullPath isn't actually under libraryRoot - fall back to the full
            // path rather than showing something confusing.
            return relative.StartsWith("..", StringComparison.Ordinal) ? fullPath : relative;
        }
        catch (ArgumentException)
        {
            return fullPath;
        }
    }
}
