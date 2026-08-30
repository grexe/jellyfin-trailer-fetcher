using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>A single search/probe result: lightweight metadata without a download.</summary>
/// <param name="Title">The video's title, as reported by the extractor.</param>
/// <param name="DurationSeconds">The video's duration in seconds, if known.</param>
/// <param name="WebpageUrl">The video's canonical webpage URL, used for the follow-up download.</param>
public record YtDlpCandidate(string Title, double? DurationSeconds, string WebpageUrl);

/// <summary>
/// Shells out to the yt-dlp executable to probe candidate videos (metadata only, no
/// download) and to download a chosen one. yt-dlp is not bundled with the plugin -
/// it must be installed wherever the Jellyfin server process itself runs.
///
/// Two-phase probe-then-download, rather than yt-dlp's own Python-API match_filter
/// callback (not available via the CLI): a probe fully extracts metadata for every
/// candidate in a source (e.g. the top 5 "ytsearch5:" results) without downloading, the
/// caller filters candidates in-process (see TitleMatching), and only the accepted
/// candidate is downloaded - functionally equivalent bandwidth-wise, since yt-dlp's own
/// match_filter also fully extracts metadata before deciding to skip a download.
/// </summary>
public class YtDlpClient
{
    private readonly string _ytDlpExecutable;
    private readonly string? _denoPath;
    private readonly string? _cookiesFilePath;
    private readonly string? _ffmpegDir;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpClient"/> class.
    /// </summary>
    /// <param name="ytDlpExecutable">The resolved yt-dlp command or path to invoke (managed, or the admin's own override).</param>
    /// <param name="denoPath">Path to a managed deno executable, if one was provisioned; null to fall back to a bare "deno"/"node" PATH lookup.</param>
    /// <param name="cookiesFilePath">Path to a Netscape-format cookies.txt, if configured.</param>
    /// <param name="ffmpegDir">Directory containing Jellyfin's own ffmpeg binary, reused by yt-dlp for muxing.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public YtDlpClient(string ytDlpExecutable, string? denoPath, string? cookiesFilePath, string? ffmpegDir, ILogger logger)
    {
        _ytDlpExecutable = ytDlpExecutable;
        _denoPath = denoPath;
        _cookiesFilePath = cookiesFilePath;
        _ffmpegDir = ffmpegDir;
        _logger = logger;
    }

    private List<string> CommonArgs()
    {
        var args = new List<string>
        {
            "--ignore-config",
            "--no-playlist",
            "--no-check-certificates",
            "--geo-bypass",
            "--socket-timeout", "15",
            "--remote-components", "ejs:github"
        };

        // A managed deno's exact path is passed explicitly so it works regardless of
        // PATH; otherwise fall back to a bare PATH lookup (deno first, node as a
        // secondary fallback) in case the admin installed one manually.
        if (!string.IsNullOrEmpty(_denoPath))
        {
            args.Add("--js-runtimes");
            args.Add($"deno:{_denoPath}");
        }
        else
        {
            args.Add("--js-runtimes");
            args.Add("deno");
            args.Add("--js-runtimes");
            args.Add("node");
        }

        // Only web-family clients honor cookies for authenticated/age-restricted access;
        // android/ios don't support cookies at all. Keep them as fallbacks only when
        // there are no cookies to use.
        var hasCookies = !string.IsNullOrEmpty(_cookiesFilePath) && File.Exists(_cookiesFilePath);
        var playerClients = hasCookies ? "web" : "web,android,ios";
        args.Add("--extractor-args");
        args.Add($"youtube:player_client={playerClients}");

        if (hasCookies)
        {
            args.Add("--cookies");
            args.Add(_cookiesFilePath!);
        }

        if (!string.IsNullOrEmpty(_ffmpegDir))
        {
            args.Add("--ffmpeg-location");
            args.Add(_ffmpegDir);
        }

        return args;
    }

    /// <summary>Fully extract metadata (title, duration, webpage_url) for every entry in a source, without downloading.</summary>
    public async Task<List<YtDlpCandidate>> ProbeAsync(string source, CancellationToken cancellationToken)
    {
        // A probe only needs title/duration/webpage_url - it never downloads anything -
        // but yt-dlp still resolves a format internally even for "-j --skip-download",
        // and hard-fails the whole extraction if it can't (e.g. "Requested format is
        // not available"), discarding metadata that was otherwise fully extracted.
        // --ignore-no-formats-error keeps that metadata instead of losing a real,
        // possibly-matching candidate over a format problem that doesn't matter yet -
        // DownloadAsync (which does need a real format) still fails normally if this
        // candidate turns out to not actually be downloadable.
        var args = new List<string> { "-j", "--skip-download", "--ignore-no-formats-error" };
        args.AddRange(CommonArgs());
        args.Add(source);

        var (exitCode, stdout, stderr) = await RunAsync(args, cancellationToken).ConfigureAwait(false);

        var candidates = new List<YtDlpCandidate>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind is JsonValueKind.Number
                    ? d.GetDouble()
                    : null;
                var url = root.TryGetProperty("webpage_url", out var w)
                    ? w.GetString()
                    : root.TryGetProperty("original_url", out var ou)
                        ? ou.GetString()
                        : null;

                if (!string.IsNullOrEmpty(url))
                {
                    candidates.Add(new YtDlpCandidate(title, duration, url));
                }
            }
            catch (JsonException e)
            {
                _logger.LogWarning("  > Could not parse yt-dlp JSON output line: {Error}", e.Message);
            }
        }

        if (candidates.Count == 0 && exitCode != 0)
        {
            var firstError = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal));
            _logger.LogWarning("  > Source {Source} failed ({Error}). Trying next source...", source, firstError ?? $"exit code {exitCode}");
        }

        return candidates;
    }

    /// <summary>
    /// Download a specific video URL into <paramref name="destinationPath"/>. Downloads
    /// into a temp directory first, then copies to a ".part" name on the destination
    /// volume and atomically renames into place - a direct write to the final name
    /// could otherwise leave a truncated file with the "real" filename if interrupted
    /// mid-copy, which a later run would mistake for a valid trailer.
    /// </summary>
    public async Task<bool> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        var tmpDir = Directory.CreateTempSubdirectory("trailer-fetcher-");
        try
        {
            var tmpPattern = Path.Combine(tmpDir.FullName, "trailer.%(ext)s");
            var args = new List<string>
            {
                "-f", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
                "--merge-output-format", "mp4",
                "--no-part",
                "-o", tmpPattern
            };
            args.AddRange(CommonArgs());
            args.Add(url);

            var (_, _, stderr) = await RunAsync(args, cancellationToken).ConfigureAwait(false);

            var downloadedFile = Directory.EnumerateFiles(tmpDir.FullName)
                .Where(f => new FileInfo(f).Length > 0)
                .FirstOrDefault();

            if (downloadedFile is null)
            {
                var firstError = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal));
                _logger.LogWarning("  > No file downloaded for {Url} ({Error}).", url, firstError ?? "unknown error");
                return false;
            }

            var tmpDest = destinationPath + ".part";
            try
            {
                File.Copy(downloadedFile, tmpDest, overwrite: true);
                File.Move(tmpDest, destinationPath, overwrite: true);
            }
            catch (IOException e)
            {
                _logger.LogWarning("  > Copy to destination failed: {Error}", e.Message);
                return false;
            }
            finally
            {
                if (File.Exists(tmpDest))
                {
                    File.Delete(tmpDest);
                }
            }

            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
            {
                _logger.LogInformation("  > Trailer successfully saved: {Name}", Path.GetFileName(destinationPath));
                return true;
            }

            _logger.LogWarning("  > Copy failed or file is empty on remote: {Path}", destinationPath);
            return false;
        }
        finally
        {
            try
            {
                tmpDir.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leftover temp dir is harmless.
            }
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(List<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Append(e.Data).Append('\n'); };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _logger.LogError(
                "  > Could not launch yt-dlp ({Path}): {Error}",
                _ytDlpExecutable,
                e.Message);
            return (-1, string.Empty, string.Empty);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
