using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// The outcome of the most recent "Fetch Missing Trailers" task run, shown on the
/// settings page. <c>StopReason</c> is null if the run processed everything in scope;
/// otherwise why it stopped early (e.g. "Cancelled", "YouTube rate-limited this
/// session") - whatever was found up to that point is still reflected in the counts.
/// </summary>
public record RunSummary(
    [property: JsonPropertyName("completedAtUtc")] DateTime CompletedAtUtc,
    [property: JsonPropertyName("durationSeconds")] double DurationSeconds,
    [property: JsonPropertyName("stopReason")] string? StopReason,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("totalMovies")] int TotalMovies,
    [property: JsonPropertyName("scanned")] int Scanned,
    [property: JsonPropertyName("alreadyHadTrailer")] int AlreadyHadTrailer,
    [property: JsonPropertyName("downloaded")] int Downloaded,
    [property: JsonPropertyName("notFound")] int NotFound,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("renamed")] int Renamed,
    [property: JsonPropertyName("migrated")] int Migrated,
    [property: JsonPropertyName("totalSeries")] int TotalSeries = 0,
    [property: JsonPropertyName("seriesScanned")] int SeriesScanned = 0,
    [property: JsonPropertyName("seriesAlreadyHadTrailer")] int SeriesAlreadyHadTrailer = 0,
    [property: JsonPropertyName("seriesDownloaded")] int SeriesDownloaded = 0,
    [property: JsonPropertyName("seriesNotFound")] int SeriesNotFound = 0,
    [property: JsonPropertyName("seriesSkipped")] int SeriesSkipped = 0);

/// <summary>
/// Persists the most recent run's summary (the same numbers logged as the "TRAILER SYNC
/// SUMMARY" block) to a small JSON file in the plugin's data folder, so the settings page
/// can show it without an admin having to go dig through the log.
/// </summary>
public static class RunSummaryStore
{
    private const string FileName = "last-run-summary.json";

    /// <summary>Writes the given summary, overwriting any previous one.</summary>
    public static void Save(string dataFolderPath, RunSummary summary)
    {
        Directory.CreateDirectory(dataFolderPath);
        File.WriteAllText(Path.Combine(dataFolderPath, FileName), JsonSerializer.Serialize(summary));
    }

    /// <summary>Reads the last saved summary, or null if there isn't one yet or it can't be read.</summary>
    public static RunSummary? Load(string dataFolderPath)
    {
        var path = Path.Combine(dataFolderPath, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
