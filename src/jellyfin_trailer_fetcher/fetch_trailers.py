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

IGNORED_DIR_NAMES = {
    'extras', 'behind the scenes', 'deleted scenes',
    'featurettes', 'interviews', 'scenes', 'shorts', 'trailers'
}

MIN_MEDIA_SIZE_BYTES = 1024 * 1024  # 1 MB minimum for a valid movie video file


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
        "Fields": "Path,ProductionYear,PremiereDate,LocalTrailerCount,RemoteTrailers,RunTimeTicks,OriginalTitle"
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


def is_non_latin(text):
    """Check if the text contains non-Latin scripts (e.g. CJK, Cyrillic, Arabic)."""
    if not text:
        return False
    return bool(re.search(r'[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uff66-\uff9f\uac00-\ud7af\u0400-\u04ff\u0600-\u06ff]', text))


def clean_media_title(title):
    """
    Clean raw media title/filename of scene release tags, track numbers, codecs, and brackets,
    and extract year if present.
    """
    if not title:
        return "", None

    # 1. Remove bracketed release info: [...], {...}
    s = re.sub(r'\[.*?\]|\{.*?\}', ' ', title)

    # 2. Extract year (1900-2099) if present in parentheses or as standalone 4-digit number
    year = None
    year_match = re.search(r'[\(\[]\s*(19\d{2}|20\d{2})\s*[\)\]]', title)
    if year_match:
        year = year_match.group(1)
        s = re.sub(r'[\(\[]\s*' + year + r'\s*[\)\]]', ' ', s)
    else:
        # Check standalone 4-digit year bounded by delimiters or whitespace
        stand_match = re.search(r'(?:^|[\s._\-(])(19\d{2}|20\d{2})(?:$|[\s._\-)])', s)
        if stand_match:
            year = stand_match.group(1)
            s = s[:stand_match.start(1)] + " " + s[stand_match.end(1):]

    # 3. Strip leading track/episode/disc numbers: e.g. "01 ", "02 - ", "1. ", "01. ", "01_"
    s = re.sub(r'^\s*0*\d{1,3}\s*[\.\-_]\s*', '', s)
    s = re.sub(r'^\s*0*\d{1,3}\s+', '', s)

    # 4. Remove common scene / release / quality / audio noise keywords
    noise_patterns = [
        r'\b(720p|1080p|1080i|2160p|4k|uhd|hd|sd|480p|360p)\b',
        r'\b(h264|h265|x264|x265|hevc|av1|xvid|divx|10bit|8bit)\b',
        r'\b(aac|ac3|dts|dts-hd|truehd|atmos|flac|mp3|ddp|dd5\.1|5\.1)\b',
        r'\b(bluray|blu-ray|bdrip|brrip|web-dl|webrip|web|dvdrip|dvd|hdtv|remux)\b',
        r'\b(eng|jpn|ger|fra|spa|ita|multi|subs?|dub|dual audio|subbed|dubbed)\b',
        r'\b(animation|anime)\b',
        r'\b(mp4|mkv|avi|vob|iso)\b',
    ]
    for np in noise_patterns:
        s = re.sub(np, ' ', s, flags=re.IGNORECASE)

    # 5. Clean up stray punctuation and collapse whitespace
    s = re.sub(r'[_\.]', ' ', s)
    s = re.sub(r'\s*-\s*$', '', s)
    s = re.sub(r'^\s*-\s*', '', s)
    s = re.sub(r'\s+', ' ', s).strip()

    return s, year


def extract_main_title(title):
    """
    Extract clean title without year, primary main title (before subtitle separators),
    and primary first word for fuzzy matching.
    """
    clean_t, _ = clean_media_title(title)
    clean = clean_t if clean_t else title
    # Split by subtitle delimiters: ' - ', ' : ', ' – ', ' — ', ' / ', ' | '
    parts = re.split(r'\s+[-–—:|/]\s+|\s*[:/]\s*', clean)
    main = parts[0].strip() if parts else clean
    raw_first = clean.split()[0].strip() if clean.split() else clean
    first_word = re.sub(r'[^\w\s]', '', raw_first).strip()
    return clean, main, first_word


def resolve_movie_titles(movie, local_path=""):
    """
    Determine preferred title for naming/renaming (honoring Latin locale over CJK/non-Latin)
    and collect all title variants for search and trailer filtering.
    """
    raw_name = movie.get("Name", "Unknown")
    original_title = movie.get("OriginalTitle", "")

    file_stem = ""
    if local_path:
        base = os.path.basename(local_path)
        stem = os.path.splitext(base)[0]
        file_stem = stem

    # Clean the raw title, original title, and file stem of scene tags
    cleaned_name, _ = clean_media_title(raw_name)
    cleaned_orig, _ = clean_media_title(original_title)
    cleaned_stem, _ = clean_media_title(file_stem)

    name_cand = cleaned_name or raw_name
    orig_cand = cleaned_orig or original_title
    stem_cand = cleaned_stem or file_stem

    # Preferred title for display and file naming:
    if not is_non_latin(name_cand):
        preferred_title = name_cand
    elif orig_cand and not is_non_latin(orig_cand):
        preferred_title = orig_cand
    elif stem_cand and not is_non_latin(stem_cand):
        preferred_title = stem_cand
    else:
        preferred_title = name_cand

    # Collect title variants for query generation and filter cross-checks
    title_variants = []
    if not is_non_latin(preferred_title):
        ordered_candidates = [preferred_title, orig_cand, stem_cand, raw_name]
    else:
        ordered_candidates = [raw_name, orig_cand, stem_cand, preferred_title]

    for t in ordered_candidates:
        if t and t not in title_variants:
            title_variants.append(t)

    return preferred_title, title_variants


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


def get_trailer_sources(movie, local_path=""):
    """
    Determine sources for trailer download:
    1. Official RemoteTrailers from Jellyfin metadata
    2. Multi-stage YouTube search (Full title + year, Main title + year, Broad query, Native search)
    """
    year = movie.get("ProductionYear")
    if not year and movie.get("PremiereDate"):
        year = movie.get("PremiereDate")[:4]

    if not year and local_path:
        _, ext_yr_name = clean_media_title(movie.get("Name", ""))
        _, ext_yr_file = clean_media_title(os.path.basename(local_path))
        year = ext_yr_name or ext_yr_file

    remote_trailers = movie.get("RemoteTrailers", [])
    sources_to_try = []

    # 1. Add official remote links first
    for rt in remote_trailers:
        url = rt.get("Url") if isinstance(rt, dict) else str(rt)
        if url and ("youtube" in url.lower() or "youtu.be" in url.lower()):
            if url not in sources_to_try:
                sources_to_try.append(url)

    # 2. Collect title candidates
    _, title_variants = resolve_movie_titles(movie, local_path)

    queries = []
    for cand in title_variants:
        clean_cand, main_cand, _ = extract_main_title(cand)
        non_lat = is_non_latin(clean_cand)

        if non_lat:
            # Japanese / CJK specific search queries
            if year:
                queries.append(f"{clean_cand} {year} 予告")
            queries.append(f"{clean_cand} 予告")
            queries.append(f"{clean_cand} PV")
            if main_cand != clean_cand:
                queries.append(f"{main_cand} 予告")
                queries.append(f"{main_cand} PV")
        else:
            # Latin / English search queries
            # Stage 1: Official trailer with year
            if year:
                queries.append(f"{clean_cand} {year} official trailer")
                if main_cand != clean_cand:
                    queries.append(f"{main_cand} {year} official trailer")

            # Stage 2: Trailer search without year (handles unlisted/differing release years)
            queries.append(f"{clean_cand} official trailer")
            queries.append(f"{clean_cand} trailer")
            if main_cand != clean_cand:
                queries.append(f"{main_cand} official trailer")
                queries.append(f"{main_cand} trailer")

            # Stage 3: Broad query
            if year:
                queries.append(f"{clean_cand} {year}")
                if main_cand != clean_cand:
                    queries.append(f"{main_cand} {year}")
            if main_cand != clean_cand:
                queries.append(main_cand)

    # Add deduplicated search queries (ytsearch5 checks top 5 candidates with max_downloads=1)
    for q in queries:
        q_strip = q.strip()
        if q_strip:
            source = f"ytsearch5:{q_strip}"
            if source not in sources_to_try:
                sources_to_try.append(source)

    return sources_to_try


# A release year right after a matched title is normal ("Inception 2010 Trailer").
# A short number/roman-numeral/"Part N" is not - it usually means the search matched
# the title of a *different* installment of a franchise (e.g. candidate "Iron Man"
# matching inside "Iron Man 2 Official Trailer").
_YEAR_SUFFIX_RE = re.compile(r'^(19\d{2}|20\d{2})\b')
_SEQUEL_SUFFIX_RE = re.compile(r'^(\d{1,2}|ii|iii|iv|v|vi|vii|viii|ix|x|part\s*\d{1,2}|chapter\s*\d{1,2})\b')


def _title_suffix_allowed(remainder):
    """Whether the text right after a matched title candidate still counts as a match."""
    if not remainder or _YEAR_SUFFIX_RE.match(remainder):
        return True
    return not _SEQUEL_SUFFIX_RE.match(remainder)


def create_trailer_filter(title_variants, movie_duration_sec, is_search):
    """Create a yt-dlp match_filter function for trailers."""
    if isinstance(title_variants, str):
        title_variants = [title_variants]

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
            allowed = ['trailer', 'teaser', 'vorschau', 'preview', 'clip', 'pv', '予告', '特報', '本予告', 'cm', 'sub']
            keyword_match = any(kw in yt_title for kw in allowed)

            norm_yt = re.sub(r'\s+', ' ', re.sub(r'[^\w\s]', ' ', yt_title)).strip()

            title_match = False
            for tv in title_variants:
                clean_t, main_t, first_w = extract_main_title(tv)
                # For Latin titles, only try first_w as its own candidate when the main
                # title genuinely is a single word (e.g. "DAKAICHI"). Otherwise first_w
                # is just the first word of a longer main_t (e.g. "Iron" from "Iron
                # Man") - too generic to match on alone without risking a wrong movie.
                # Non-Latin (e.g. Japanese) titles keep first_w unconditionally, since
                # PV/teaser titles commonly use just the short proper-noun title and
                # accidental collisions with an unrelated common word are far less
                # likely there than with short English words.
                if is_non_latin(clean_t) or main_t == first_w:
                    candidates = [clean_t, main_t, first_w]
                else:
                    candidates = [clean_t, main_t]
                for cand in candidates:
                    cand_lower = cand.lower().strip()
                    if not cand_lower:
                        continue
                    if is_non_latin(cand_lower):
                        if cand_lower in yt_title:
                            title_match = True
                            break
                    else:
                        norm_cand = re.sub(r'\s+', ' ', re.sub(r'[^\w\s]', ' ', cand_lower)).strip()
                        if not norm_cand:
                            continue
                        phrase_match = re.search(r'\b' + re.escape(norm_cand) + r'\b', norm_yt)
                        if phrase_match:
                            if _title_suffix_allowed(norm_yt[phrase_match.end():].strip()):
                                title_match = True
                                break
                            # The full phrase matched, but is immediately followed by what
                            # looks like a different installment's number - reject this
                            # candidate rather than falling through to the looser word-list
                            # check below, which would match just as wrongly.
                            continue
                        # Phrase not contiguous - fall back to requiring every significant
                        # word to appear as a whole word (not a substring) somewhere in the
                        # title, e.g. "Cars" must not match inside "Scars".
                        ignore_words = {'the', 'a', 'an', 'der', 'die', 'das', 'le', 'la', 'les', 'el', 'los', 'il', 'lo', 'and', 'of', 'in', 'on', 'at', 'to', 'for', 'with'}
                        words = [w for w in norm_cand.split() if len(w) > 1 and w not in ignore_words]
                        if words and all(re.search(r'\b' + re.escape(w) + r'\b', norm_yt) for w in words):
                            title_match = True
                            break
                if title_match:
                    break

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
        # 'web' must come first: only web-family clients honor cookiesfrombrowser for
        # authenticated/age-restricted access. android/ios are unauthenticated fallbacks
        # kept for videos that don't need sign-in. (Note: yt-dlp's Python API expects
        # this as a nested dict, not the "key=value" strings used on the CLI - passing
        # the CLI-style strings here is silently ignored.)
        'extractor_args': {
            'youtube': {
                'player_client': ['web', 'android', 'ios'],
            }
        }
    }
    if cookie_browser:
        ydl_opts['cookiesfrombrowser'] = (cookie_browser,)

    return ydl_opts


def trigger_jellyfin_library_scan(jellyfin_url, api_key):
    """Trigger a full Jellyfin library scan (equivalent to the "Scan All Libraries"
    dashboard button).

    Discovering a newly-added local trailer file is a filesystem-resolution step that
    happens during a library scan, not during a per-item metadata refresh
    (/Items/{id}/Refresh only re-fetches metadata/images for an item already in the DB
    and does not reliably pick up new sibling files). Call this once after all movies
    have been processed rather than once per item.
    """
    url = f"{jellyfin_url}/Library/Refresh"
    headers = get_jellyfin_headers(api_key)
    try:
        response = requests.post(url, headers=headers, timeout=15)
        response.raise_for_status()
        return True
    except Exception as e:
        logger.error(f"Failed to trigger Jellyfin library scan: {e}")
        return False


def print_summary(state, total_movies, dry_run=False, rename_original=False):
    """Print execution statistics summary."""
    logger.info("")
    logger.info("==========================================")
    logger.info("           TRAILER SYNC SUMMARY           ")
    logger.info("==========================================")
    logger.info(f"  Total Movies in Library : {total_movies}")
    logger.info(f"  Movies Processed        : {state.get('scanned', 0)}")
    logger.info(f"  Already had Trailer     : {state.get('already_had_trailer', 0)}")
    if dry_run:
        logger.info(f"  Trailers Found (Dry-Run): {state.get('downloaded', 0)}")
    else:
        logger.info(f"  Trailers Downloaded     : {state.get('downloaded', 0)}")
    logger.info(f"  No Trailer Found        : {state.get('not_found', 0)}")
    if state.get('skipped', 0) > 0:
        logger.info(f"  Skipped (Unreachable)   : {state.get('skipped', 0)}")
    if rename_original and state.get('renamed', 0) > 0:
        logger.info(f"  Original Files Renamed  : {state.get('renamed', 0)}")
    logger.info("==========================================")


def process_movie(movie, args, state, config):
    """Process a single movie: check eligibility, search & download trailer."""
    _, _, path_mappings, cookie_browser = config
    raw_title = movie.get("Name", "Unknown")
    path = movie.get("Path", "")
    
    # 1. Check & map path before logging
    local_path = translate_path(path, path_mappings)
    if not local_path:
        state['skipped'] = state.get('skipped', 0) + 1
        return

    # Validate that it is a valid main media file (not trailer, sample, extra, missing)
    valid, reason = is_valid_media_file(local_path)
    if not valid:
        logger.warning(f"Skipping '{raw_title}': {reason} ({local_path})")
        state['skipped'] = state.get('skipped', 0) + 1
        return

    state['scanned'] = state.get('scanned', 0) + 1

    preferred_title, title_variants = resolve_movie_titles(movie, local_path)

    folder_path = os.path.dirname(local_path)
    
    # Track directory changes to reduce log noise
    if state.get('last_dir') != folder_path:
        logger.info(f"\n*** Entering directory: {folder_path}")
        state['last_dir'] = folder_path

    logger.info(f"Processing movie file: {os.path.basename(local_path)} ...")
    
    year = movie.get("ProductionYear")
    if not year and movie.get("PremiereDate"):
        year = movie.get("PremiereDate")[:4]

    if not year and local_path:
        _, ext_yr_name = clean_media_title(movie.get("Name", ""))
        _, ext_yr_file = clean_media_title(os.path.basename(local_path))
        year = ext_yr_name or ext_yr_file
    
    year_str = f" ({year})" if year else ""
    safe_title = sanitize_filename(f"{preferred_title}{year_str}")

    logger.info(f"  > using title '{preferred_title}'")
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
        state['already_had_trailer'] = state.get('already_had_trailer', 0) + 1
        return

    # 3. OPTIONAL: Rename original file
    if getattr(args, "rename_original", False) and local_path != new_movie_path:
        if args.dry_run:
            logger.info(f"  > [DRY-RUN] Would rename original file to: '{os.path.basename(new_movie_path)}'")
            state['renamed'] = state.get('renamed', 0) + 1
        else:
            if not os.path.exists(new_movie_path):
                try:
                    os.rename(local_path, new_movie_path)
                    logger.info(f"  > Original file renamed to: '{os.path.basename(new_movie_path)}'")
                    local_path = new_movie_path
                    state['renamed'] = state.get('renamed', 0) + 1
                except Exception as e:
                    logger.error(f"  > Failed to rename file for '{preferred_title}': {e}")
            else:
                logger.warning(f"  > Target file '{os.path.basename(new_movie_path)}' already exists. Skipping rename.")

    # 4. Determine sources for trailer download
    sources_to_try = get_trailer_sources(movie, local_path)

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

        current_trailer_filter = create_trailer_filter(title_variants, movie_duration_sec, is_search)

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
                # Copy to a temp name on the destination volume first, then atomically
                # rename into place. A direct copy2() to the final name would leave a
                # truncated file with the "real" filename if interrupted mid-copy (e.g.
                # an NFS hiccup), and a later run would mistake it for a valid trailer.
                tmp_dest = trailer_filename + ".part"
                try:
                    shutil.copy2(downloaded_file, tmp_dest)
                    os.replace(tmp_dest, trailer_filename)
                except OSError as e:
                    logger.warning(f"  > Copy to destination failed: {e}")
                finally:
                    if os.path.exists(tmp_dest):
                        os.remove(tmp_dest)

                if os.path.exists(trailer_filename) and os.path.getsize(trailer_filename) > 0:
                    logger.info(f"  > Trailer successfully saved: {os.path.basename(trailer_filename)}")
                    download_success = True
                    break
                else:
                    logger.warning(f"  > Copy failed or file is empty on remote: {trailer_filename}")
            else:
                logger.warning(f"  > No suitable trailer found for source ({source}).")

    if download_success:
        state['downloaded'] = state.get('downloaded', 0) + 1
    else:
        state['not_found'] = state.get('not_found', 0) + 1


def main(argv=None):
    parser = argparse.ArgumentParser(description="Download missing movie trailers for Jellyfin.")
    parser.add_argument("--dry-run", action="store_true", help="Show what would happen without modifying anything.")
    parser.add_argument("--sync", action="store_true", help="Trigger a single Jellyfin library scan after all downloads complete.")
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

    # Dictionary to maintain state and statistics across the loop iterations
    state = {
        'last_dir': None,
        'scanned': 0,
        'already_had_trailer': 0,
        'downloaded': 0,
        'not_found': 0,
        'skipped': 0,
        'renamed': 0,
    }

    for movie in movies:
        process_movie(movie, args, state, config)

    # Print summary statistics at the end
    print_summary(state, len(movies), dry_run=args.dry_run, rename_original=args.rename_original)

    # Trigger a single library scan (not one per movie) so Jellyfin picks up every
    # newly downloaded trailer file in one pass.
    if args.sync and not args.dry_run:
        if state.get('downloaded', 0) > 0:
            logger.info(f"Triggering a Jellyfin library scan to pick up {state['downloaded']} new trailer(s)...")
            trigger_jellyfin_library_scan(jellyfin_url, api_key)
        else:
            logger.info("No new trailers downloaded; skipping Jellyfin library scan.")


if __name__ == "__main__":
    main()
