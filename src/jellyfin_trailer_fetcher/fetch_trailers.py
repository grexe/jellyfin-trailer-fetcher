import os
import sys
import argparse
import logging
import requests
import tempfile
import yt_dlp
from yt_dlp.utils import MaxDownloadsReached
import json
import re
import shutil
from dotenv import load_dotenv

# Set up logging in English
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger(__name__)

VIDEO_EXTENSIONS = {
    '.mp4', '.mkv', '.avi', '.mov', '.wmv', '.m4v',
    '.webm', '.ts', '.m2ts', '.iso', '.vob'
}


class YoutubeDownloaderLogger:
    def debug(self, msg):
        # We ignore verbose debug lines from yt-dlp to keep the log clean
        pass

    def info(self, msg):
        if "Video filtered out by match_filter" in msg or "Filtered:" in msg:
            logger.info(f"[yt-dlp filter] {msg}")
        else:
            logger.info(f"[yt-dlp] {msg}")

    def warning(self, msg):
        logger.warning(f"[yt-dlp] {msg}")

    def error(self, msg):
        logger.error(f"[yt-dlp] {msg}")


def load_config(load_env=True):
    """Load configuration from environment variables (.env)."""
    if load_env:
        load_dotenv()

    jellyfin_url = os.getenv("JELLYFIN_URL", "").rstrip("/")
    api_key = os.getenv("API_KEY", "")
    
    path_mappings_raw = os.getenv("PATH_MAPPINGS")
    path_mappings = {}

    if path_mappings_raw:
        try:
            path_mappings = json.loads(path_mappings_raw)
        except json.JSONDecodeError as e:
            logger.critical(f"Error: PATH_MAPPINGS in .env is not valid JSON: {e}")
            return None, None, None, None
    else:
        # Fallback to single prefix configuration if present
        nas_prefix = os.getenv("NAS_PATH_PREFIX")
        mac_prefix = os.getenv("MAC_PATH_PREFIX")
        if nas_prefix and mac_prefix:
            path_mappings = {nas_prefix: mac_prefix}

    cookie_browser = os.getenv("COOKIE_BROWSER", "firefox")
    if cookie_browser and cookie_browser.lower() in ("none", "false", "0", ""):
        cookie_browser = None

    return jellyfin_url, api_key, path_mappings, cookie_browser


def get_jellyfin_headers(api_key):
    return {"Authorization": f'MediaBrowser Token="{api_key}"'}


def get_jellyfin_movies(jellyfin_url, api_key):
    """Fetch all movies from Jellyfin API."""
    logger.info("Fetching movie metadata from Jellyfin API...")
    url = f"{jellyfin_url}/Items"
    headers = get_jellyfin_headers(api_key)
    params = {
        "IncludeItemTypes": "Movie",
        "Recursive": "true",
        "Fields": "Path,ProductionYear,PremiereDate,LocalTrailerCount,RemoteTrailers,RunTimeTicks"
    }
    
    try:
        response = requests.get(url, headers=headers, params=params, timeout=15)
        response.raise_for_status()
        movies = response.json().get("Items", [])
        # Sort movies by path to group directories for cleaner logging
        return sorted(movies, key=lambda x: x.get("Path", "") or "")
    except requests.exceptions.RequestException as e:
        logger.error(f"Error fetching metadata: {e}")
        return []


def translate_path(nas_path, path_mappings):
    """Map NAS path prefix to local Mac path prefix."""
    if not nas_path or not path_mappings:
        return None
    for nas_prefix, mac_prefix in path_mappings.items():
        if not nas_prefix.endswith('/'):
            nas_prefix += '/'
        if not mac_prefix.endswith('/'):
            mac_prefix += '/'
            
        if nas_path.startswith(nas_prefix):
            return nas_path.replace(nas_prefix, mac_prefix, 1)
        
    return None


def sanitize_filename(name):
    """Remove illegal filesystem characters from filename."""
    clean = re.sub(r'[\\/*?:"<>|]', "", name).strip()
    return clean if clean else "Unknown_Movie"


IGNORED_DIR_NAMES = {
    'extras', 'behind the scenes', 'deleted scenes',
    'featurettes', 'interviews', 'scenes', 'shorts', 'trailers'
}

MIN_MEDIA_SIZE_BYTES = 1024 * 1024  # 1 MB minimum for a valid movie video file


def is_valid_media_file(local_path, min_size_bytes=MIN_MEDIA_SIZE_BYTES):
    """Check if the local path is a valid main movie video file."""
    if not os.path.exists(local_path):
        return False, "File does not exist"

    if not os.path.isfile(local_path):
        return False, "Path is not a regular file"

    ext = os.path.splitext(local_path)[1].lower()
    if ext not in VIDEO_EXTENSIONS:
        return False, f"Not a video file (extension: {ext})"

    # Sanity check file size (avoid 0-byte or corrupted stub files)
    try:
        file_size = os.path.getsize(local_path)
        if file_size < min_size_bytes:
            return False, f"File size too small ({file_size} bytes < {min_size_bytes} bytes)"
    except OSError as e:
        return False, f"Could not determine file size: {e}"

    filename = os.path.basename(local_path)
    filename_lower = filename.lower()
    stem = os.path.splitext(filename_lower)[0]

    # Exclude trailers
    if stem == "trailer" or stem.endswith(("-trailer", "_trailer", ".trailer")):
        return False, "File is already a trailer"

    # Exclude sample clips
    if stem == "sample" or stem.endswith(("-sample", "_sample", ".sample")) or ".sample." in filename_lower:
        return False, "File is a sample clip"

    # Exclude files inside extras / featurettes subdirectories
    path_parts = [p.lower() for p in os.path.normpath(local_path).split(os.sep)]
    if any(part in IGNORED_DIR_NAMES for part in path_parts[:-1]):
        return False, "File is located inside an extras directory"

    return True, None


def get_trailer_sources(movie):
    """
    Determine sources for trailer download:
    1. Official RemoteTrailers from Jellyfin metadata
    2. Two-stage YouTube search (Official Trailer query, then Broad query)
    """
    title = movie.get("Name", "Unknown")
    year = movie.get("ProductionYear")
    if not year and movie.get("PremiereDate"):
        year = movie.get("PremiereDate")[:4]

    remote_trailers = movie.get("RemoteTrailers", [])
    sources_to_try = []

    # Add official remote links first
    for rt in remote_trailers:
        url = rt.get("Url") if isinstance(rt, dict) else str(rt)
        if url and ("youtube" in url.lower() or "youtu.be" in url.lower()):
            sources_to_try.append(url)

    # Clean title for search (strip any existing (YYYY) in title)
    clean_title = re.sub(r'[\(\[]\s*\d{4}\s*[\)\]]', '', title).strip()

    # Add two-stage search using ytsearch5 (yt-dlp checks top 5 candidates with max_downloads=1)
    # Stage 1: Official Trailer query
    search_query_official = f"{clean_title} {year} official trailer".strip() if year else f"{clean_title} official trailer"
    sources_to_try.append(f"ytsearch5:{search_query_official}")
    
    # Stage 2: Broad query (only if Stage 1 fails)
    search_query_broad = f"{clean_title} {year}".strip() if year else clean_title
    sources_to_try.append(f"ytsearch5:{search_query_broad}")

    return sources_to_try


def create_trailer_filter(title, movie_duration_sec, is_search):
    """Create a yt-dlp match_filter function for trailers."""
    def current_trailer_filter(info, *, incomplete):
        # When incomplete is True, full metadata (like title or duration) is not yet available.
        # Returning None lets yt-dlp continue to extract complete metadata.
        if incomplete:
            return None

        duration = info.get('duration')
        yt_title = (info.get('title') or "").lower()

        # 1. Universal Duration Check (Trailers are rarely > 5 minutes / 300s)
        if duration and duration > 300:
            return f'Duration > 5min ({duration}s)'

        # 2. Movie Duration Check (10,000,000 ticks = 1 second in Jellyfin RunTimeTicks)
        if movie_duration_sec and movie_duration_sec > 60:
            if duration and duration >= (movie_duration_sec * 0.6):
                return f'Too long compared to movie ({duration}s >= {movie_duration_sec}s)'

        # 3. Keyword/Title Matching for Search Results
        if is_search:
            clean_title = re.sub(r'[\(\[]\s*\d{4}\s*[\)\]]', '', title).strip().lower()
            allowed = ['trailer', 'teaser', 'vorschau', 'preview', 'clip']

            # Normalize punctuation and collapse whitespace
            norm_target = re.sub(r'\s+', ' ', re.sub(r'[^\w\s]', ' ', clean_title)).strip()
            norm_yt = re.sub(r'\s+', ' ', re.sub(r'[^\w\s]', ' ', yt_title)).strip()

            # Significant words check (ignore common articles)
            ignore_words = {'the', 'a', 'an', 'der', 'die', 'das', 'le', 'la', 'les', 'el', 'los', 'il', 'lo'}
            target_words = [w for w in norm_target.split() if len(w) > 1 and w not in ignore_words]
            words_match = all(w in norm_yt for w in target_words) if target_words else (norm_target in norm_yt)

            title_match = (clean_title in yt_title) or (norm_target in norm_yt) or words_match
            keyword_match = any(kw in yt_title for kw in allowed)

            if not (title_match and keyword_match):
                return f'Rejected. Title mismatch or missing keyword: "{yt_title}"'

        return None

    return current_trailer_filter


def build_ydl_opts(outtmpl_pattern, filter_func, cookie_browser="firefox"):
    """Construct yt-dlp options dictionary with optimal settings and geobypass."""
    ydl_opts = {
        # Force format to standard m4a/aac to avoid experimental codecs like iamf
        'format': 'bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best',
        'outtmpl': outtmpl_pattern,
        'noplaylist': True,
        'max_downloads': 1,
        'logger': YoutubeDownloaderLogger(),
        'merge_output_format': 'mp4',
        'no_part': True,
        'nocheckcertificate': True,
        'remote_components': ['ejs:github'],
        'js_runtimes': {'deno': {}, 'node': {}},
        'socket_timeout': 15,
        'geo_bypass': True,
        'match_filter': filter_func,
        'extractor_args': {
            'youtube': ['player_client=android,web,ios', 'po_token=generated']
        }
    }
    if cookie_browser:
        ydl_opts['cookiesfrombrowser'] = (cookie_browser,)

    return ydl_opts


def trigger_jellyfin_refresh(jellyfin_url, api_key, item_id):
    """Trigger a metadata refresh in Jellyfin for a specific item."""
    url = f"{jellyfin_url}/Items/{item_id}/Refresh"
    headers = get_jellyfin_headers(api_key)
    params = {"Recursive": "true", "MetadataRefreshMode": "Default", "ImageRefreshMode": "Default"}
    try:
        response = requests.post(url, headers=headers, params=params, timeout=10)
        response.raise_for_status()
        return True
    except Exception as e:
        logger.error(f"Failed to trigger API sync for item ID '{item_id}': {e}")
        return False


def process_movie(movie, args, state, config):
    """Process a single movie: check eligibility, search & download trailer, sync."""
    jellyfin_url, api_key, path_mappings, cookie_browser = config
    title = movie.get("Name", "Unknown")
    path = movie.get("Path", "")
    
    # 1. Check & map path before logging
    local_path = translate_path(path, path_mappings)
    if not local_path:
        return

    # Validate that it is a valid main media file (not trailer, sample, extra, missing)
    valid, reason = is_valid_media_file(local_path)
    if not valid:
        logger.warning(f"Skipping '{title}': {reason} ({local_path})")
        return

    folder_path = os.path.dirname(local_path)
    
    # Track directory changes to reduce log noise
    if state.get('last_dir') != folder_path:
        logger.info(f"\n*** Entering directory: {folder_path}")
        state['last_dir'] = folder_path

    logger.info(f"Processing movie file: {os.path.basename(local_path)} ...")
    
    year = movie.get("ProductionYear")
    if not year and movie.get("PremiereDate"):
        year = movie.get("PremiereDate")[:4]
    
    year_str = f" ({year})" if year else ""
    safe_title = sanitize_filename(f"{title}{year_str}")

    logger.info(f"  > using title '{title}'")
    original_ext = os.path.splitext(local_path)[1]
    new_movie_path = os.path.join(folder_path, f"{safe_title}{original_ext}")
    trailer_filename = os.path.join(folder_path, f"{safe_title}-trailer.mp4")

    # Calculate actual movie duration in seconds (10,000,000 ticks = 1 second)
    runtime_ticks = movie.get("RunTimeTicks")
    movie_duration_sec = (runtime_ticks / 10000000) if runtime_ticks else None

    # 2. Check if local trailer exists (metadata count or file existence)
    trailer_candidates = [
        trailer_filename,
        os.path.join(folder_path, f"{safe_title}-trailer.mkv"),
        os.path.join(folder_path, "trailer.mp4"),
        os.path.join(folder_path, "trailer.mkv")
    ]
    if movie.get("LocalTrailerCount", 0) > 0 or any(os.path.exists(cand) for cand in trailer_candidates):
        logger.info("  > Trailer already exists, skipping.")
        return

    # 3. OPTIONAL: Rename original file
    if getattr(args, "rename_original", False) and local_path != new_movie_path:
        if args.dry_run:
            logger.info(f"  > [DRY-RUN] Would rename original file to: '{os.path.basename(new_movie_path)}'")
        else:
            if not os.path.exists(new_movie_path):
                try:
                    os.rename(local_path, new_movie_path)
                    logger.info(f"  > Original file renamed to: '{os.path.basename(new_movie_path)}'")
                    local_path = new_movie_path
                except Exception as e:
                    logger.error(f"  > Failed to rename file for '{title}': {e}")
            else:
                logger.warning(f"  > Target file '{os.path.basename(new_movie_path)}' already exists. Skipping rename.")

    # 4. Determine sources for trailer download
    sources_to_try = get_trailer_sources(movie)

    # 5. yt-dlp Configuration & Improved Filter Logic
    log_prefix = "[DRY-RUN] " if args.dry_run else ""
    download_success = False

    for source in sources_to_try:
        is_search = source.startswith("ytsearch")
        logger.info(f"  > {log_prefix}Fetching trailer via {'Search' if is_search else 'Remote-URL'} ({source})...")

        if args.dry_run:
            logger.info(f"  > [DRY-RUN] Will save as: '{os.path.basename(trailer_filename)}'")
            download_success = True
            break

        current_trailer_filter = create_trailer_filter(title, movie_duration_sec, is_search)

        with tempfile.TemporaryDirectory() as tmp_dir:
            tmp_pattern = os.path.join(tmp_dir, "trailer.%(ext)s")
            ydl_opts = build_ydl_opts(tmp_pattern, current_trailer_filter, cookie_browser=cookie_browser)

            try:
                with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                    ydl.download([source])
            except MaxDownloadsReached:
                # MaxDownloadsReached is raised when max_downloads limit is reached, indicating success
                pass
            except Exception as e:
                # Video unavailable / private / network error on this source; proceed to next source
                logger.warning(f"  > Source '{source}' failed ({e}). Trying next source...")

            # Check if a file was downloaded in tmp_dir
            downloaded_files = [
                os.path.join(tmp_dir, f) for f in os.listdir(tmp_dir)
                if os.path.isfile(os.path.join(tmp_dir, f)) and os.path.getsize(os.path.join(tmp_dir, f)) > 0
            ]

            if downloaded_files:
                downloaded_file = downloaded_files[0]
                shutil.copy2(downloaded_file, trailer_filename)
                if os.path.exists(trailer_filename) and os.path.getsize(trailer_filename) > 0:
                    logger.info(f"  > Trailer successfully saved: {os.path.basename(trailer_filename)}")
                    download_success = True
                    break
                else:
                    logger.warning(f"  > Copy failed or file is empty on remote: {trailer_filename}")
            else:
                logger.warning(f"  > No suitable trailer found for source ({source}).")

    # 6. API-Sync trigger
    if download_success and getattr(args, "sync", False) and not args.dry_run:
        item_id = movie.get("Id")
        if item_id:
            logger.info(f"  > Triggering Jellyfin metadata refresh for '{title}' (ID: {item_id})...")
            trigger_jellyfin_refresh(jellyfin_url, api_key, item_id)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Download missing movie trailers for Jellyfin.")
    parser.add_argument("--dry-run", action="store_true", help="Show what would happen without modifying anything.")
    parser.add_argument("--sync", action="store_true", help="Trigger a Jellyfin metadata scan after download.")
    parser.add_argument("--rename-original", action="store_true", help="Rename the original movie file to match the metadata title.")
    args = parser.parse_args(argv)

    config = load_config()
    jellyfin_url, api_key, path_mappings, _ = config

    if not jellyfin_url or not api_key or not path_mappings:
        logger.critical("Missing configuration in .env (JELLYFIN_URL, API_KEY, or PATH_MAPPINGS).")
        sys.exit(1)

    if args.dry_run:
        logger.info("DRY-RUN MODE ACTIVE")

    movies = get_jellyfin_movies(jellyfin_url, api_key)
    logger.info(f"-> {len(movies)} movies found in Jellyfin database.")

    # Dictionary to maintain state across the loop iterations
    state = {'last_dir': None}

    for movie in movies:
        process_movie(movie, args, state, config)


if __name__ == "__main__":
    main()
