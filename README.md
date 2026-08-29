# Jellyfin Trailer Fetcher

A lightweight local Python tool that fetches movie metadata from a Jellyfin instance, searches for missing movie trailers on YouTube, downloads them using `yt-dlp` (with geofencing & bot-bypass workarounds), saves them alongside local media files (`<movie-name>-trailer.mp4`), and optionally triggers a Jellyfin library refresh.

## Features

* **Direct Trailer Fetching:** Solves permission & embedding errors on clients (e.g. webOS Jellyfin app) by storing trailers locally.
* **Smart Source Determination:** Uses official Jellyfin `RemoteTrailers` links first, followed by a two-stage search strategy (official trailer query, then broad fallback).
* **Cross-Check Filtering:** Validates duration (≤ 5 min, < 60% of movie length) and verifies keywords (`trailer`, `teaser`, `vorschau`, `preview`, `clip`) against normalized titles.
* **Media Validation:** Validates video media files and ignores sample files, cover art, extras, or existing trailers.
* **NFS/NAS Path Mapping:** Translates Jellyfin server paths to local mount points.
* **Dry-Run Mode:** Safely preview actions without downloading files or making changes.
* **Jellyfin Sync:** Optionally triggers a single full Jellyfin library scan after all trailers have been downloaded, so newly added local trailer files are picked up.

## Prerequisites

* Python 3.8+ (or 3.14+)
* [uv](https://github.com/astral-sh/uv) (fast Python package manager)
* `ffmpeg` (required by `yt-dlp` to merge video and audio streams)

### macOS Setup

```bash
brew install uv ffmpeg
```

## Setup

1. **Clone repository & navigate into project:**
   ```bash
   git clone <repo-url>
   cd jellyfin-trailer-fetcher
   ```

2. **Install dependencies:**
   ```bash
   uv sync
   ```

3. **Configure environment:**
   ```bash
   cp .env.sample .env
   ```
   Edit `.env` and set `JELLYFIN_URL`, `API_KEY`, and `PATH_MAPPINGS`.

## Usage

Run via `uv`:

- **Dry-run (preview only, no modifications):**
  ```bash
  uv run jellyfin-trailer-fetcher --dry-run
  ```

- **Dry-run with simulated sync:**
  ```bash
  uv run jellyfin-trailer-fetcher --dry-run --sync
  ```

- **Download missing trailers:**
  ```bash
  uv run jellyfin-trailer-fetcher
  ```

- **Download trailers & trigger Jellyfin refresh:**
  ```bash
  uv run jellyfin-trailer-fetcher --sync
  ```

- **Rename original files to match metadata title:**
  ```bash
  uv run jellyfin-trailer-fetcher --rename-original
  ```

## Running Tests

```bash
uv run python -m unittest discover -s tests
```
