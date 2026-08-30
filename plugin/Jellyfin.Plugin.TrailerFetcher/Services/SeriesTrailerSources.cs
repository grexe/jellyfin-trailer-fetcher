using System.Collections.Generic;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// Builds the ordered list of sources to try for a TV series' trailer: official
/// RemoteTrailers first, then a multi-stage set of YouTube searches. Deliberately a
/// separate implementation from <see cref="TrailerSources"/> rather than a shared one -
/// see <see cref="SeriesMetadata"/> for why.
/// </summary>
public static class SeriesTrailerSources
{
    /// <summary>Build the ordered list of sources: direct YouTube URLs first, then "ytsearch5:&lt;query&gt;" strings.</summary>
    public static List<string> Build(Series series, List<string> titleVariants, string? year)
    {
        var sourcesToTry = new List<string>();

        foreach (var rt in series.RemoteTrailers)
        {
            var url = rt.Url;
            if (!string.IsNullOrEmpty(url) &&
                (url.Contains("youtube", System.StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", System.StringComparison.OrdinalIgnoreCase)) &&
                !sourcesToTry.Contains(url))
            {
                sourcesToTry.Add(url);
            }
        }

        var queries = new List<string>();
        foreach (var cand in titleVariants)
        {
            var (cleanCand, mainCand, _) = TitleMatching.ExtractMainTitle(cand);
            var nonLatin = TitleMatching.IsNonLatin(cleanCand);

            if (nonLatin)
            {
                if (year is not null)
                {
                    queries.Add($"{cleanCand} {year} 予告");
                }

                queries.Add($"{cleanCand} 予告");
                queries.Add($"{cleanCand} PV");
                if (mainCand != cleanCand)
                {
                    queries.Add($"{mainCand} 予告");
                    queries.Add($"{mainCand} PV");
                }
            }
            else
            {
                if (year is not null)
                {
                    queries.Add($"{cleanCand} {year} official trailer");
                    if (mainCand != cleanCand)
                    {
                        queries.Add($"{mainCand} {year} official trailer");
                    }
                }

                queries.Add($"{cleanCand} official trailer");
                queries.Add($"{cleanCand} trailer");
                if (mainCand != cleanCand)
                {
                    queries.Add($"{mainCand} official trailer");
                    queries.Add($"{mainCand} trailer");
                }

                if (year is not null)
                {
                    queries.Add($"{cleanCand} {year}");
                    if (mainCand != cleanCand)
                    {
                        queries.Add($"{mainCand} {year}");
                    }
                }

                if (mainCand != cleanCand)
                {
                    queries.Add(mainCand);
                }
            }
        }

        foreach (var q in queries)
        {
            var qStrip = q.Trim();
            if (qStrip.Length == 0)
            {
                continue;
            }

            var source = $"ytsearch5:{qStrip}";
            if (!sourcesToTry.Contains(source))
            {
                sourcesToTry.Add(source);
            }
        }

        return sourcesToTry;
    }
}
