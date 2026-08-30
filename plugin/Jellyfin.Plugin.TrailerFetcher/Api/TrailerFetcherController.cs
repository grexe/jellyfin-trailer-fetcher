using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrailerFetcher.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.Api;

/// <summary>
/// A library the admin can pick to scope scanning to, as shown on the settings page.
/// </summary>
/// <param name="Id">The library's ItemId (a Guid, as a string).</param>
/// <param name="Name">The library's display name.</param>
/// <param name="CollectionType">The library's configured content type (e.g. "movies"), if any.</param>
/// <remarks>
/// Property names are pinned explicitly to camelCase via <see cref="JsonPropertyNameAttribute"/>
/// rather than relying on Jellyfin's host-level JSON casing configuration (which serializes
/// most of its own API PascalCase), so the settings page's JS can rely on a fixed casing
/// regardless of how that global option is set.
/// </remarks>
public record LibraryInfoDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("collectionType")] string? CollectionType);

/// <summary>
/// Handles uploading/removing the yt-dlp cookies file, and listing libraries, from the
/// plugin's settings page. A headless server has no browser profile to read cookies
/// from directly (unlike the standalone script's --cookie-browser), so authenticated/
/// age-restricted YouTube access instead relies on an exported Netscape-format
/// cookies.txt uploaded here.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("TrailerFetcher")]
public class TrailerFetcherController : ControllerBase
{
    private const string CookiesFileName = "cookies.txt";
    private const long MaxCookiesFileBytes = 2 * 1024 * 1024; // 2 MB is generous for a cookie jar

    private readonly ILogger<TrailerFetcherController> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrailerFetcherController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{TrailerFetcherController}"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    public TrailerFetcherController(ILogger<TrailerFetcherController> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Lists the server's libraries, for the settings page to offer as scan-scope choices.
    /// </summary>
    /// <returns>The list of libraries.</returns>
    [HttpGet("Libraries")]
    public ActionResult<IEnumerable<LibraryInfoDto>> GetLibraries()
    {
        var libraries = _libraryManager.GetVirtualFolders()
            .Select(f => new LibraryInfoDto(f.ItemId, f.Name, f.CollectionType?.ToString()))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(libraries);
    }

    /// <summary>
    /// Returns the outcome of the most recent "Fetch Missing Trailers" run, for the
    /// settings page to display without digging through the log.
    /// </summary>
    /// <returns>The last run's summary, or 204 if the task hasn't run yet.</returns>
    [HttpGet("LastRunSummary")]
    public ActionResult<RunSummary> GetLastRunSummary()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
        var summary = RunSummaryStore.Load(plugin.DataFolderPath);
        return summary is null ? NoContent() : Ok(summary);
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
