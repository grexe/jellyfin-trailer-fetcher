# Trailer Fetcher (Jellyfin plugin)

Native C# rewrite of the standalone [`jellyfin-trailer-fetcher`](../README.md) script,
running in-process on the Jellyfin server.

**Current status:** settings page, cookie upload, and library-scoping are wired up and
testable. The actual trailer search/download/migration logic (ported from the Python
script) has not landed yet - `FetchTrailersTask` currently only logs what it *would*
scan, so installing this build will not download any trailers.

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

### Option B - plugin repository (what you'll actually use long-term)

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

## Development

```bash
dotnet build          # verify it compiles against the real Jellyfin 10.10.7 packages
dotnet publish -c Release -o publish   # what package.sh does internally
```

No local Jellyfin server is required to build or verify compilation - only to actually
run the plugin.
