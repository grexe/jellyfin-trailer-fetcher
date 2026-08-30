using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// Resolves a TV series' display title, title variants (for search/matching), and
/// release year from its Jellyfin metadata and its own folder name. Deliberately a
/// separate, independent implementation from <see cref="MovieMetadata"/> rather than a
/// shared/generic one, even though the logic looks similar - movies and series are
/// different enough in practice (a series always has its own dedicated folder already,
/// there's no single "runtime" to compare a trailer against, etc.) that sharing code
/// risks a change made for one silently affecting the other. The local path passed to
/// these methods is the series' own folder path (<c>series.Path</c>) - there's no
/// separate "file" the way a movie has one, but the same folder-name-vs-metadata trust
/// heuristic still applies (e.g. a mistakenly-matched series' folder name is still the
/// more reliable signal), so this is deliberately reused as if it were the "file stem".
/// </summary>
public static class SeriesMetadata
{
    /// <summary>
    /// Determine the series' start year. Prefers the year embedded in the series'
    /// folder name over Jellyfin's ProductionYear/PremiereDate when the two disagree.
    /// </summary>
    public static string? ResolveYear(Series series, string localPath)
    {
        var folderStem = localPath.Length > 0 ? Path.GetFileNameWithoutExtension(localPath) : string.Empty;
        var (_, folderYear) = TitleMatching.CleanMediaTitle(folderStem);

        string? metadataYear = series.ProductionYear?.ToString();
        if (string.IsNullOrEmpty(metadataYear) && series.PremiereDate.HasValue)
        {
            metadataYear = series.PremiereDate.Value.Year.ToString();
        }

        if (!string.IsNullOrEmpty(folderYear) && !string.IsNullOrEmpty(metadataYear) && folderYear != metadataYear)
        {
            return folderYear;
        }

        if (!string.IsNullOrEmpty(metadataYear))
        {
            return metadataYear;
        }

        if (!string.IsNullOrEmpty(folderYear))
        {
            return folderYear;
        }

        var (_, nameYear) = TitleMatching.CleanMediaTitle(series.Name);
        return nameYear;
    }

    /// <summary>
    /// Determine the preferred title for naming the trailer file (honoring Latin locale
    /// over CJK/non-Latin) and collect all title variants for search and trailer
    /// filtering.
    /// </summary>
    public static (string PreferredTitle, List<string> TitleVariants) ResolveTitles(Series series, string localPath)
    {
        var rawName = string.IsNullOrEmpty(series.Name) ? "Unknown" : series.Name;
        var originalTitle = series.OriginalTitle ?? string.Empty;
        var folderStem = localPath.Length > 0 ? Path.GetFileNameWithoutExtension(localPath) : string.Empty;

        var (cleanedName, _) = TitleMatching.CleanMediaTitle(rawName);
        var (cleanedOrig, _) = TitleMatching.CleanMediaTitle(originalTitle);
        var (cleanedStem, _) = TitleMatching.CleanMediaTitle(folderStem);

        var nameCand = cleanedName.Length > 0 ? cleanedName : rawName;
        var origCand = cleanedOrig.Length > 0 ? cleanedOrig : originalTitle;
        var stemCand = cleanedStem.Length > 0 ? cleanedStem : folderStem;

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
