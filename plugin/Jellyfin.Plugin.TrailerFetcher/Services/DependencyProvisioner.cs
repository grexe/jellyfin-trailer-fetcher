using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// Downloads and keeps up to date the two executables trailer fetching needs - yt-dlp
/// itself and a JavaScript runtime (deno) it requires for YouTube's player challenges -
/// so the plugin works out of the box on a stock Jellyfin container instead of requiring
/// a customized image. Neither is bundled inside the plugin's own zip: yt-dlp
/// specifically needs frequent updates to keep working against YouTube's changes, so a
/// copy frozen at plugin-release time would go stale in weeks; fetching both lazily
/// from their own GitHub releases keeps the plugin package itself small and each
/// dependency independently up to date.
///
/// Only used when the admin leaves the "yt-dlp executable" setting empty - setting it to
/// a specific command/path (e.g. a system-installed yt-dlp) opts back out of all of this
/// and uses that instead, unmanaged.
/// </summary>
public class DependencyProvisioner
{
    private const string YtDlpChecksumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private static readonly TimeSpan SelfUpdateInterval = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _binDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyProvisioner"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="dataFolderPath">The plugin's own data folder, under which managed binaries are stored.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public DependencyProvisioner(IHttpClientFactory httpClientFactory, string dataFolderPath, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _binDir = Path.Combine(dataFolderPath, "bin");
    }

    /// <summary>
    /// Ensures a managed yt-dlp executable exists, downloading it on first use, and
    /// self-updates it (via yt-dlp's own "-U") at most once per <see cref="SelfUpdateInterval"/>.
    /// </summary>
    public async Task<string?> EnsureYtDlpAsync(CancellationToken cancellationToken)
    {
        var assetName = OperatingSystem.IsWindows()
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "yt-dlp_arm64.exe" : "yt-dlp.exe")
            : OperatingSystem.IsMacOS()
                ? "yt-dlp_macos"
                : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";

        var destination = Path.Combine(_binDir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

        if (!File.Exists(destination))
        {
            var downloadUrl = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{assetName}";
            if (!await DownloadVerifiedAsync(downloadUrl, destination, assetName, YtDlpChecksumsUrl, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            MakeExecutable(destination);
            _logger.LogInformation("  > yt-dlp downloaded to {Path}.", destination);
        }
        else
        {
            await SelfUpdateIfDueAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        return destination;
    }

    /// <summary>
    /// Ensures a managed deno executable exists, downloading it on first use. Unlike
    /// yt-dlp, deno itself doesn't need frequent updates (it's YouTube's own extraction
    /// code, not the runtime, that changes constantly), so this only downloads once.
    /// </summary>
    public async Task<string?> EnsureDenoAsync(CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_binDir, OperatingSystem.IsWindows() ? "deno.exe" : "deno");
        if (File.Exists(destination))
        {
            return destination;
        }

        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
        var triple = OperatingSystem.IsWindows() ? $"{arch}-pc-windows-msvc"
            : OperatingSystem.IsMacOS() ? $"{arch}-apple-darwin"
            : $"{arch}-unknown-linux-gnu";
        var zipName = $"deno-{triple}.zip";
        var downloadUrl = $"https://github.com/denoland/deno/releases/latest/download/{zipName}";
        var checksumUrl = $"{downloadUrl}.sha256sum";

        var tmpZip = Path.Combine(_binDir, zipName + ".part");
        Directory.CreateDirectory(_binDir);
        if (!await DownloadVerifiedAsync(downloadUrl, tmpZip, zipName, checksumUrl, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var extractDir = Path.Combine(_binDir, "deno-extract");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            ZipFile.ExtractToDirectory(tmpZip, extractDir);
            var extracted = Path.Combine(extractDir, OperatingSystem.IsWindows() ? "deno.exe" : "deno");
            if (!File.Exists(extracted))
            {
                _logger.LogWarning("  > deno archive did not contain the expected executable.");
                return null;
            }

            File.Move(extracted, destination, overwrite: true);
            Directory.Delete(extractDir, recursive: true);
            MakeExecutable(destination);
            _logger.LogInformation("  > deno downloaded to {Path}.", destination);
            return destination;
        }
        finally
        {
            if (File.Exists(tmpZip))
            {
                File.Delete(tmpZip);
            }
        }
    }

    private async Task SelfUpdateIfDueAsync(string ytDlpPath, CancellationToken cancellationToken)
    {
        var marker = Path.Combine(_binDir, ".last-update-check");
        if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < SelfUpdateInterval)
        {
            return;
        }

        _logger.LogInformation("  > Checking for a yt-dlp update...");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ytDlpPath, "-U")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(_binDir);
            await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _logger.LogWarning("  > yt-dlp self-update check failed (non-fatal): {Error}", e.Message);
        }
    }

    private async Task<bool> DownloadVerifiedAsync(string url, string destination, string assetName, string checksumUrl, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_binDir);
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        string? expectedHash;
        try
        {
            expectedHash = await FindChecksumAsync(client, checksumUrl, assetName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("  > Could not fetch checksum for '{Asset}' ({Error}); proceeding without verification.", assetName, e.Message);
            expectedHash = null;
        }

        _logger.LogInformation("  > Downloading {Asset}...", assetName);
        var tmpFile = destination + ".download";
        try
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = File.Create(tmpFile);
                await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            if (expectedHash is not null)
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(tmpFile), cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("  > Checksum mismatch for '{Asset}': expected {Expected}, got {Actual}.", assetName, expectedHash, actualHash);
                    File.Delete(tmpFile);
                    return false;
                }
            }

            File.Move(tmpFile, destination, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogError("  > Failed to download '{Asset}': {Error}", assetName, e.Message);
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }

            return false;
        }
    }

    private static async Task<string?> FindChecksumAsync(HttpClient client, string checksumUrl, string assetName, CancellationToken cancellationToken)
    {
        var content = await client.GetStringAsync(checksumUrl, cancellationToken).ConfigureAwait(false);

        // yt-dlp's SHA2-256SUMS lists every asset, one per line: "<hash>  <filename>".
        // deno's per-asset ".sha256sum" sidecar file has just one line, either just the
        // hash or "<hash>  <filename>" too - both formats are handled by taking the
        // first token of the line that mentions our asset name (or the only line, if
        // the file has just one and doesn't mention a filename at all).
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = lines.FirstOrDefault(l => l.Contains(assetName, StringComparison.Ordinal)) ?? lines.FirstOrDefault();
        return line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
