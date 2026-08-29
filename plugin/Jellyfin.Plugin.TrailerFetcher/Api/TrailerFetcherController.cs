using System;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.Api;

/// <summary>
/// Handles uploading/removing the yt-dlp cookies file from the plugin's settings page.
/// A headless server has no browser profile to read cookies from directly (unlike the
/// standalone script's --cookie-browser), so authenticated/age-restricted YouTube
/// access instead relies on an exported Netscape-format cookies.txt uploaded here.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("TrailerFetcher")]
public class TrailerFetcherController : ControllerBase
{
    private const string CookiesFileName = "cookies.txt";
    private const long MaxCookiesFileBytes = 2 * 1024 * 1024; // 2 MB is generous for a cookie jar

    private readonly ILogger<TrailerFetcherController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerFetcherController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TrailerFetcherController}"/> interface.</param>
    public TrailerFetcherController(ILogger<TrailerFetcherController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Uploads a Netscape-format cookies.txt file, storing it in the plugin's data
    /// folder and pointing the configuration at it.
    /// </summary>
    /// <param name="file">The uploaded cookies.txt file.</param>
    /// <returns>The path the file was saved to.</returns>
    [HttpPost("Cookies")]
    [RequestSizeLimit(MaxCookiesFileBytes)]
    public async Task<ActionResult<string>> UploadCookies(IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("No file was uploaded.");
        }

        if (file.Length > MaxCookiesFileBytes)
        {
            return BadRequest("Cookies file is too large.");
        }

        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        Directory.CreateDirectory(plugin.DataFolderPath);
        var destinationPath = Path.Combine(plugin.DataFolderPath, CookiesFileName);

        await using (var stream = System.IO.File.Create(destinationPath))
        {
            await file.CopyToAsync(stream).ConfigureAwait(false);
        }

        plugin.Configuration.CookiesFilePath = destinationPath;
        plugin.SaveConfiguration();

        _logger.LogInformation("Cookies file uploaded to {Path} ({Bytes} bytes).", destinationPath, file.Length);
        return Ok(destinationPath);
    }

    /// <summary>
    /// Removes the currently configured cookies file, if any, and clears the setting.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Cookies")]
    public ActionResult RemoveCookies()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        var path = plugin.Configuration.CookiesFilePath;

        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }

        plugin.Configuration.CookiesFilePath = string.Empty;
        plugin.SaveConfiguration();

        _logger.LogInformation("Cookies file removed.");
        return NoContent();
    }
}
