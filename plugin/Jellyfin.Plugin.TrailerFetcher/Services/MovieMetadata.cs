using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// Resolves a movie's display title, title variants (for search/matching), and release
/// year from its Jellyfin metadata and its file's own name - ported from the standalone
/// script's resolve_movie_titles/resolve_movie_year. Unlike the standalone script (an
/// external process talking to the Jellyfin HTTP API over a possibly-translated NAS
/// path), this runs inside the server itself: the local path is simply
/// <c>movie.Path</c>, no path mapping needed.
/// </summary>
public static class MovieMetadata
{
    /// <summary>
    /// Determine the movie's release year. Prefers the year embedded in the file's own
    /// name over Jellyfin's ProductionYear/PremiereDate when the two disagree - a movie
    /// correctly named "Chang An (2023).mkv" on disk should not get treated as a 2012
    /// film just because Jellyfin matched it to a same-named 2012 film's metadata.
    /// </summary>
    public static string? ResolveYear(Movie movie, string localPath)
    {
        var fileStem = localPath.Length > 0 ? Path.GetFileNameWithoutExtension(localPath) : string.Empty;
        var (_, fileYear) = TitleMatching.CleanMediaTitle(fileStem);

        string? metadataYear = movie.ProductionYear?.ToString();
        if (string.IsNullOrEmpty(metadataYear) && movie.PremiereDate.HasValue)
        {
            metadataYear = movie.PremiereDate.Value.Year.ToString();
        }

        if (!string.IsNullOrEmpty(fileYear) && !string.IsNullOrEmpty(metadataYear) && fileYear != metadataYear)
        {
            return fileYear;
        }

        if (!string.IsNullOrEmpty(metadataYear))
        {
            return metadataYear;
        }

        if (!string.IsNullOrEmpty(fileYear))
        {
            return fileYear;
        }

        var (_, nameYear) = TitleMatching.CleanMediaTitle(movie.Name);
        return nameYear;
    }

    /// <summary>
    /// Determine the preferred title for naming/renaming (honoring Latin locale over
    /// CJK/non-Latin) and collect all title variants for search and trailer filtering.
    /// </summary>
    public static (string PreferredTitle, List<string> TitleVariants) ResolveTitles(Movie movie, string localPath)
    {
        var rawName = string.IsNullOrEmpty(movie.Name) ? "Unknown" : movie.Name;
        var originalTitle = movie.OriginalTitle ?? string.Empty;
        var fileStem = localPath.Length > 0 ? Path.GetFileNameWithoutExtension(localPath) : string.Empty;

        var (cleanedName, _) = TitleMatching.CleanMediaTitle(rawName);
        var (cleanedOrig, _) = TitleMatching.CleanMediaTitle(originalTitle);
        var (cleanedStem, _) = TitleMatching.CleanMediaTitle(fileStem);

        var nameCand = cleanedName.Length > 0 ? cleanedName : rawName;
        var origCand = cleanedOrig.Length > 0 ? cleanedOrig : originalTitle;
        var stemCand = cleanedStem.Length > 0 ? cleanedStem : fileStem;

        // Jellyfin's "Name" metadata can be wrong in a way that no amount of noise-
        // stripping fixes (a bad provider match) - trust the filename instead when it
        // looks untrustworthy. See TitleMatching.PreferFilenameOverMetadata.
        var distrustMetadata = TitleMatching.PreferFilenameOverMetadata(nameCand, stemCand);
        if (distrustMetadata)
        {
            nameCand = stemCand;
        }

        string preferredTitle;
        if (!TitleMatching.IsNonLatin(nameCand))
        {
            preferredTitle = nameCand;
        }
        else if (!string.IsNullOrEmpty(origCand) && !TitleMatching.IsNonLatin(origCand))
        {
            preferredTitle = origCand;
        }
        else if (!string.IsNullOrEmpty(stemCand) && !TitleMatching.IsNonLatin(stemCand))
        {
            preferredTitle = stemCand;
        }
        else
        {
            preferredTitle = nameCand;
        }

        List<string> orderedCandidates;
        if (distrustMetadata)
        {
            // A wrong Name usually means Jellyfin matched this item to an entirely
            // different movie, so OriginalTitle and the raw Name are from that same
            // wrong match too and can't be trusted either as fallback candidates.
            orderedCandidates = [preferredTitle];
        }
        else if (!TitleMatching.IsNonLatin(preferredTitle))
        {
            orderedCandidates = [preferredTitle, origCand, stemCand, rawName];
        }
        else
        {
            orderedCandidates = [rawName, origCand, stemCand, preferredTitle];
        }

        var titleVariants = new List<string>();
        foreach (var t in orderedCandidates)
        {
            if (!string.IsNullOrEmpty(t) && !titleVariants.Contains(t, StringComparer.Ordinal))
            {
                titleVariants.Add(t);
            }
        }

        return (preferredTitle, titleVariants);
    }
}
