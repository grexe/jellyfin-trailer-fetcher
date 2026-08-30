<p align="center">
  <img src="images/logo.png" alt="Trailer Fetcher logo" width="200" />
</p>

# Trailer Fetcher (Jellyfin plugin)

Finds and downloads missing local movie trailers from YouTube via `yt-dlp`, running
natively in-process on the Jellyfin server, with sequel-aware title matching and
automatic folder migration - Jellyfin only recognizes a local trailer file when the
movie has its own dedicated folder ([jellyfin/jellyfin#10077](https://github.com/jellyfin/jellyfin/issues/10077)).

This is a from-scratch C# port of the standalone
[`jellyfin-trailer-fetcher`](https://codeberg.org/grexe/jellyfin-trailer-fetcher/src/branch/main)
Python script, not a wrapper around it - running server-side changes the shape of the
problem enough that a straight rewrite made more sense than shelling out.

## Why a native rewrite, not a wrapper

The script was built to run on a separate machine (a Mac) against a remote Jellyfin
server over its NAS mount, and that shaped a lot of its design: `PATH_MAPPINGS` to
translate NAS paths to a local mount, a Jellyfin API key for every read *and* write,
`--cookie-browser` to read a logged-in browser's cookie jar, and REST calls (including
a full library scan) to get Jellyfin to notice anything changed.

None of that applies once the code runs *inside* Jellyfin's own process:

- **No path translation.** The plugin sees the exact same filesystem Jellyfin does -
  `ILibraryManager` items already carry the real, local path.
- **No API key, no REST round-trips to itself.** Library queries, refreshes, and scans
  go through `ILibraryManager`/`IServerApplicationHost` directly.
- **No browser cookie jar.** A headless server has no logged-in Firefox profile to read
  from, so authenticated/age-restricted YouTube access instead uses an exported
  Netscape-format `cookies.txt`, uploaded through the settings page.
- **Per-library scoping** becomes possible for free, since the plugin can query
  `ILibraryManager.GetVirtualFolders()` directly instead of guessing from paths.

The title-matching, filtering, and folder-migration *logic* itself - the part that took
the most iteration to get right (sequel-vs-original confusion, trusting a mismatched
title's own other fields, etc.) - carries over essentially unchanged, just reimplemented
in C#.

## Current status

| Area | Status |
|---|---|
| Plugin/task/config wiring | ✅ Working - settings page loads/saves, scheduled task registers |
| Cookie file upload | ✅ Working |
| Per-library scan scoping | ✅ Working |
| Dedicated log file | ✅ Working |
| Trailer search/filter/download | ⏳ Not ported yet |
| Folder migration & rename | ⏳ Not ported yet |

`FetchTrailersTask` currently only enumerates the configured scope and logs what it
*would* do - installing this build will not download any trailers yet.

## Installing for testing

### Option A - quick local copy (fastest iteration)

```bash
./package.sh
```

This produces `dist/Jellyfin.Plugin.TrailerFetcher_<version>.zip`. Unzip it into your
Jellyfin server's plugin directory, in its own version-named subfolder:

```
<jellyfin data dir>/plugins/Trailer Fetcher_<version>/Jellyfin.Plugin.TrailerFetcher.dll
<jellyfin data dir>/plugins/Trailer Fetcher_<version>/Jellyfin.Plugin.TrailerFetcher.pdb
```

Then restart Jellyfin. `<jellyfin data dir>` is wherever Jellyfin's `ProgramDataPath` is
mounted for your install - for a Docker/TrueNAS SCALE app, that's the container path
your app's config volume is mapped to (commonly `/config` inside the container); check
your app's storage/volume mapping to find the host path. Repeat after every change: no
update notification, no version tracking - just overwrite and restart.

### Option B - plugin repository (the real install/update path)

This uses Jellyfin's normal install/update mechanism instead of manual file copying.

1. Run `./package.sh` and copy the printed entry into `manifest.json`'s `versions`
   array (replacing the previous entry, or adding a new one to keep history).
2. Commit `dist/` and `manifest.json`, push.
3. In Jellyfin: **Dashboard → Plugins → Repositories → Add Repository**, using this
   raw URL:
   ```
   https://codeberg.org/grexe/jellyfin-trailer-fetcher/raw/branch/plugin/plugin/manifest.json
   ```
4. **Dashboard → Plugins → Catalog** should now list "Trailer Fetcher" - install it
   from there. Future `package.sh` + manifest update + push cycles show up as a normal
   update in the Jellyfin dashboard.

Note this points at the `plugin` branch, which can move/rebase during development - if
Jellyfin ever reports a checksum mismatch after a force-push, remove and re-add the
plugin. Once this stabilizes, switch the repository URL to a tagged release instead.

## Library scoping

The settings page lists every configured Jellyfin library (fetched live via
`GET /TrailerFetcher/Libraries`, backed by `ILibraryManager.GetVirtualFolders()`).
Leave all unchecked to scan every library; check specific ones to scope
`FetchTrailersTask`'s query to just those (`InternalItemsQuery.TopParentIds`).

## Logging

This plugin's log entries (from `FetchTrailersTask`, `TrailerFetcherController`, and
anything else in the `Jellyfin.Plugin.TrailerFetcher` namespace) are mirrored into
their own file, `<plugin data folder>/trailer-fetcher.log`, in addition to Jellyfin's
main server log - so a run can be inspected without wading through unrelated server
noise. Implemented as a scoped `ILoggerProvider` (`Logging/PluginFileLoggerProvider.cs`,
registered via `PluginServiceRegistrator`) that filters by log category rather than
requiring a second, duplicate logging call at every call site - any future code that
injects the normal `ILogger<T>` gets this for free.

## Development

```bash
dotnet build          # verify it compiles against the real Jellyfin 10.11.11 packages
dotnet publish -c Release -o publish   # what package.sh does internally
```

No local Jellyfin server is required to build or verify compilation - only to actually
run the plugin.
