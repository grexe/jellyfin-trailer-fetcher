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

    # 4. Remove common scene / release / quality / audio / language noise keywords
    noise_patterns = [
        r'\b(720p|1080p|1080i|2160p|4k|uhd|hd|sd|480p|360p)\b',
        r'\b(h264|h265|x264|x265|hevc|av1|xvid|divx|10bit|8bit)\b',
        r'\b(aac|e?ac3|e-ac-3|dts|dts-hd|dts-x|dtsx|truehd|atmos|flac|mp3|pcm|opus|ddp|dd5\.1|5\.1)\b',
        r'\b(bluray|blu-ray|bdrip|brrip|web-dl|webrip|web|dvdrip|dvd|hdtv|remux)\b',
        # ISO-ish abbreviations for common track languages
        r'\b(eng|jpn|ger|fra|spa|ita|multi|subs?|dub|dual audio|subbed|dubbed)\b',
        # Spelled-out language names, as seen in container title tags / scene releases
        r'\b(chinese|mandarin|cantonese|korean|japanese|english|german|french|spanish|'
        r'italian|russian|thai|vietnamese|hindi|arabic|portuguese|dutch|polish|turkish|'
        r'swedish|norwegian|danish|finnish|greek|hebrew|indonesian)\b',
        r'\b(animation|anime)\b',
        r'\b(mp4|mkv|avi|vob|iso)\b',
    ]
    for np in noise_patterns:
        s = re.sub(np, ' ', s, flags=re.IGNORECASE)

    # 5. Clean up stray punctuation, now-empty parenthetical remnants (e.g. a
    # "( EAC3 )" tag that noise-stripping hollowed out), and collapse whitespace
    s = re.sub(r'[_\.]', ' ', s)
    s = re.sub(r'\(\s*\)|\[\s*\]', ' ', s)
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


_STOPWORDS = {'the', 'a', 'an', 'der', 'die', 'das', 'le', 'la', 'les', 'el', 'los', 'il', 'lo', 'and', 'of', 'in', 'on', 'at', 'to', 'for', 'with'}


def _significant_words(text):
    """Lowercased, punctuation-stripped significant words (filler words excluded)."""
    norm = re.sub(r'\s+', ' ', re.sub(r'[^\w\s]', ' ', text.lower())).strip()
    return {w for w in norm.split() if len(w) > 1 and w not in _STOPWORDS}


def _prefer_filename_over_metadata(name_cand, stem_cand):
    """
    Whether a movie's filename-derived title/year should be trusted over Jellyfin's
    "Name"-derived metadata: both are Latin-script, differ, the filename is substantial
    (not just "movie"), and less than half of the metadata's own significant words are
    shared with it. A bad provider match (wrong Name) usually gets the year wrong too,
    so this same signal drives both resolve_movie_titles and resolve_movie_year.
    """
    if not (
        stem_cand and not is_non_latin(stem_cand)
        and name_cand and not is_non_latin(name_cand)
        and stem_cand != name_cand
    ):
        return False

    # "Substantial" is judged on the raw word count, not the stopword-filtered one:
    # a genuine two-word title like "Chang An" must still count as substantial even
    # though "An" alone is a filler word and gets excluded from significant_words.
    if len(stem_cand.split()) < 2:
        return False

    name_words = _significant_words(name_cand)
    if not name_words:
        return False

    stem_words = _significant_words(stem_cand)
    overlap = stem_words & name_words
    return len(overlap) / len(name_words) < 0.5


def resolve_movie_year(movie, local_path=""):
    """
    Determine the movie's release year. Prefers the year embedded in the file's own
    name over Jellyfin's ProductionYear/PremiereDate when the two disagree - a movie
    correctly named "Chang An (2023).mkv" on disk should not get renamed to "(2012)"
    just because Jellyfin matched it to a same-named 2012 film's metadata. The title
    can look perfectly fine in that case (both agree on "Chang An"), so this can't
    reuse the word-overlap "does the title look wrong" signal from
    _prefer_filename_over_metadata - it compares the year directly instead.
    """
    file_stem = os.path.splitext(os.path.basename(local_path))[0] if local_path else ""
    _, file_year = clean_media_title(file_stem)

    metadata_year = movie.get("ProductionYear")
    if not metadata_year and movie.get("PremiereDate"):
        metadata_year = movie.get("PremiereDate")[:4]
    metadata_year = str(metadata_year) if metadata_year else None

    if file_year and metadata_year and file_year != metadata_year:
        return file_year

    if metadata_year:
        return metadata_year

    if file_year:
        return file_year

    _, name_year = clean_media_title(movie.get("Name", ""))
    return name_year


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

    # Jellyfin's "Name" metadata can be wrong in a way that no amount of noise-stripping
    # fixes: a bad provider match, or an embedded container title tag that's actually
    # leftover technical junk (e.g. "Chang An ( EAC3 ) CHINESE" for a movie whose file is
    # correctly named "Chang'e and the Jade Rabbit's Mid-Autumn Adventure"). Trust the
    # filename instead when it looks untrustworthy - see _prefer_filename_over_metadata.
    if _prefer_filename_over_metadata(name_cand, stem_cand):
        name_cand = stem_cand

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
    year = resolve_movie_year(movie, local_path)

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


def _title_prefix_allowed(prefix, match_word_count):
    """Whether the text right before a matched title candidate still counts as a
    match: the title's own words must be the dominant part of the string, not buried
    after substantial unrelated text. E.g. a candidate "Chang An" must not match
    inside "30,000 Miles From Chang'an (2023) Movie Trailer" just because that other,
    real, same-year movie's promo text happens to contain the phrase "Chang An" -
    a short prefix like "[ENG SUB] " is fine, four unrelated words in front aren't."""
    if not prefix:
        return True
    return len(prefix.split()) <= match_word_count


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
                            prefix_ok = _title_prefix_allowed(norm_yt[:phrase_match.start()].strip(), len(norm_cand.split()))
                            suffix_ok = _title_suffix_allowed(norm_yt[phrase_match.end():].strip())
                            if prefix_ok and suffix_ok:
                                title_match = True
                                break
                            # The full phrase matched, but is buried after substantial
                            # unrelated text or immediately followed by what looks like a
                            # different installment's number - reject this candidate rather
                            # than falling through to the looser word-list check below,
                            # which would match just as wrongly.
                            continue
                        # Phrase not contiguous - fall back to requiring every significant
                        # word to appear as a whole word (not a substring) somewhere in the
                        # title, e.g. "Cars" must not match inside "Scars".
                        words = [w for w in norm_cand.split() if len(w) > 1 and w not in _STOPWORDS]
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
    }

    # Only web-family clients honor cookiesfrombrowser for authenticated/age-restricted
    # access; android/ios don't support cookies at all and yt-dlp skips them outright
    # (with a warning) whenever cookies are configured, so listing them just adds noise
    # and a wasted request. Keep them as fallbacks only when there are no cookies to use.
    # (Note: yt-dlp's Python API expects extractor_args as a nested dict, not the
    # "key=value" strings used on the CLI - passing CLI-style strings here is silently
    # ignored.)
    player_clients = ['web'] if cookie_browser else ['web', 'android', 'ios']
    ydl_opts['extractor_args'] = {'youtube': {'player_client': player_clients}}

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
    if state.get('migrated', 0) > 0:
        logger.info(f"  Migrated to Own Folder  : {state.get('migrated', 0)}")
    logger.info("==========================================")


def rename_movie_file(local_path, safe_title, dry_run=False):
    """
    Rename the movie file to "<safe_title><ext>". If the movie already lives in its
    own dedicated folder (the folder's name matches the file's current stem - the
    same convention migrate_movie_to_own_folder creates and checks for), the folder
    is renamed to match too. Without this, renaming just the file would leave the
    folder's name stale, and a later migrate_movie_to_own_folder() call would then see
    a mismatch and nest a new, wrongly-named folder for it inside the existing one.

    Returns (new_local_path, renamed) where `renamed` is True if something was (or, in
    dry-run mode, would be) renamed.
    """
    folder_path = os.path.dirname(local_path)
    current_stem, ext = os.path.splitext(os.path.basename(local_path))

    folder_is_dedicated = os.path.basename(folder_path) == current_stem
    target_folder = folder_path
    if folder_is_dedicated and os.path.basename(folder_path) != safe_title:
        target_folder = os.path.join(os.path.dirname(folder_path), safe_title)

    target_movie_path = os.path.join(target_folder, f"{safe_title}{ext}")
    if target_movie_path == local_path:
        return local_path, False

    if dry_run:
        if target_folder != folder_path:
            logger.info(f"  > [DRY-RUN] Would rename folder + file to: '{safe_title}/{safe_title}{ext}'")
        else:
            logger.info(f"  > [DRY-RUN] Would rename original file to: '{os.path.basename(target_movie_path)}'")
        return local_path, True

    if target_folder != folder_path:
        if os.path.exists(target_folder):
            logger.warning(f"  > Target folder '{os.path.basename(target_folder)}' already exists. Skipping folder rename.")
            target_folder = folder_path
            target_movie_path = os.path.join(target_folder, f"{safe_title}{ext}")
        else:
            try:
                os.rename(folder_path, target_folder)
                logger.info(f"  > Folder renamed to: '{os.path.basename(target_folder)}'")
                local_path = os.path.join(target_folder, os.path.basename(local_path))
                folder_path = target_folder
            except OSError as e:
                logger.error(f"  > Failed to rename folder '{folder_path}': {e}")
                target_movie_path = os.path.join(folder_path, f"{safe_title}{ext}")

    if local_path == target_movie_path:
        return local_path, True

    if os.path.exists(target_movie_path):
        logger.warning(f"  > Target file '{os.path.basename(target_movie_path)}' already exists. Skipping rename.")
        return local_path, False

    try:
        os.rename(local_path, target_movie_path)
        logger.info(f"  > Original file renamed to: '{os.path.basename(target_movie_path)}'")
        return target_movie_path, True
    except OSError as e:
        logger.error(f"  > Failed to rename file for '{safe_title}': {e}")
        return local_path, False


def _is_sidecar_of(entry_stem, movie_stem):
    """Whether a filename (without extension) belongs to the given movie: an exact
    match, or a suffix attached with '.', '-' or '_' (subtitles like "Movie.eng.srt",
    artwork like "Movie-poster.jpg", or a trailer we previously created like
    "Movie-trailer.mp4")."""
    return entry_stem == movie_stem or any(
        entry_stem.startswith(movie_stem + sep) for sep in ('.', '-', '_')
    )


def migrate_movie_to_own_folder(local_path, dry_run=False, extra_stems=()):
    """
    Move a movie file - and any sidecar files that belong to it (subtitles, .nfo,
    artwork, an already-downloaded trailer) - into a dedicated subfolder named after
    the movie file itself.

    Jellyfin's local-extras resolver only recognizes a local trailer when the movie
    has its own folder; in a flat folder shared by multiple movies, a correctly named
    "<title>-trailer.mp4" is silently ignored regardless of naming
    (https://github.com/jellyfin/jellyfin/issues/10077). `extra_stems` should include
    the title-based stem used for the trailer filename in case it differs from the
    movie file's own (possibly still-messy) name.

    Returns (new_local_path, moved) where `moved` is True if something was (or, in
    dry-run mode, would be) moved. Safe to call repeatedly: a movie already living in
    its own folder is left untouched.
    """
    current_dir = os.path.dirname(local_path)
    movie_stem, _ = os.path.splitext(os.path.basename(local_path))

    # Already in its own folder - nothing to do. Keeps repeated runs idempotent.
    if os.path.basename(current_dir) == movie_stem:
        return local_path, False

    target_dir = os.path.join(current_dir, movie_stem)
    stems_to_match = {movie_stem, *[s for s in extra_stems if s]}

    try:
        sibling_names = os.listdir(current_dir)
    except OSError as e:
        logger.warning(f"  > Could not list '{current_dir}' for migration: {e}")
        return local_path, False

    files_to_move = []
    for name in sibling_names:
        entry_path = os.path.join(current_dir, name)
        if not os.path.isfile(entry_path):
            continue
        entry_stem, _ = os.path.splitext(name)
        if any(_is_sidecar_of(entry_stem, stem) for stem in stems_to_match):
            files_to_move.append(name)

    if not files_to_move:
        return local_path, False

    if dry_run:
        logger.info(f"  > [DRY-RUN] Would move into own folder '{movie_stem}/': {', '.join(files_to_move)}")
        return local_path, True

    try:
        os.makedirs(target_dir, exist_ok=True)
    except OSError as e:
        logger.error(f"  > Failed to create folder '{target_dir}': {e}")
        return local_path, False

    new_local_path = local_path
    moved_count = 0
    for name in files_to_move:
        src = os.path.join(current_dir, name)
        dst = os.path.join(target_dir, name)
        if os.path.exists(dst):
            logger.warning(f"  > Migration target '{dst}' already exists, leaving '{src}' in place.")
            continue
        try:
            shutil.move(src, dst)
            moved_count += 1
            if src == local_path:
                new_local_path = dst
        except OSError as e:
            logger.error(f"  > Failed to move '{src}' to '{dst}': {e}")

    if moved_count:
        logger.info(f"  > Moved {moved_count} file(s) into own folder: '{movie_stem}/'")
    return new_local_path, moved_count > 0


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
    
    year = resolve_movie_year(movie, local_path)

    year_str = f" ({year})" if year else ""
    safe_title = sanitize_filename(f"{preferred_title}{year_str}")

    logger.info(f"  > using title '{preferred_title}'")
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
    already_had_trailer = movie.get("LocalTrailerCount", 0) > 0 or any(os.path.exists(cand) for cand in trailer_candidates)
    download_success = False

    if already_had_trailer:
        logger.info("  > Trailer already exists, skipping.")
        state['already_had_trailer'] = state.get('already_had_trailer', 0) + 1
    else:
        # 3. OPTIONAL: Rename original file (and its folder, if it's already dedicated
        # to this movie - see rename_movie_file for why that matters)
        if getattr(args, "rename_original", False):
            local_path, renamed = rename_movie_file(local_path, safe_title, dry_run=args.dry_run)
            if renamed:
                state['renamed'] = state.get('renamed', 0) + 1
                # The rename may have moved the movie into a renamed folder; recompute
                # paths derived from the folder so the download below lands correctly.
                folder_path = os.path.dirname(local_path)
                trailer_filename = os.path.join(folder_path, f"{safe_title}-trailer.mp4")

        # 4. Determine sources for trailer download
        sources_to_try = get_trailer_sources(movie, local_path)

        # 5. yt-dlp Configuration & Improved Filter Logic
        log_prefix = "[DRY-RUN] " if args.dry_run else ""

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

    # 6. OPTIONAL: migrate into a dedicated per-movie folder. Jellyfin's local-extras
    # resolver silently ignores a correctly-named "<title>-trailer" file sitting in a
    # folder shared by multiple movies - it only recognizes one when the movie has its
    # own folder (https://github.com/jellyfin/jellyfin/issues/10077). "all" migrates
    # every movie; "trailers" only migrates movies that actually have a trailer
    # (pre-existing or just downloaded), leaving the rest of a flat library untouched.
    migrate_mode = getattr(args, "migrate_to_folders", None)
    should_migrate = migrate_mode == "all" or (migrate_mode == "trailers" and (already_had_trailer or download_success))
    if should_migrate:
        _, moved = migrate_movie_to_own_folder(local_path, dry_run=args.dry_run, extra_stems=(safe_title,))
        if moved:
            state['migrated'] = state.get('migrated', 0) + 1


def main(argv=None):
    parser = argparse.ArgumentParser(description="Download missing movie trailers for Jellyfin.")
    parser.add_argument("--dry-run", action="store_true", help="Show what would happen without modifying anything.")
    parser.add_argument("--sync", action="store_true", help="Trigger a single Jellyfin library scan after all downloads complete.")
    parser.add_argument("--rename-original", action="store_true", help="Rename the original movie file to match the metadata title.")
    parser.add_argument(
        "--migrate-to-folders", choices=["all", "trailers"], default=None,
        help="Move movies into a dedicated '<title>/<title>.ext' subfolder, required for "
             "Jellyfin to recognize a local trailer (see jellyfin/jellyfin#10077). "
             "'all' migrates every movie; 'trailers' only migrates movies that already "
             "have, or just got, a trailer."
    )
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
    # newly downloaded trailer file, and any moved/migrated paths, in one pass.
    if args.sync and not args.dry_run:
        downloaded = state.get('downloaded', 0)
        migrated = state.get('migrated', 0)
        if downloaded > 0 or migrated > 0:
            reasons = []
            if downloaded > 0:
                reasons.append(f"{downloaded} new trailer(s)")
            if migrated > 0:
                reasons.append(f"{migrated} migrated movie(s)")
            logger.info(f"Triggering a Jellyfin library scan to pick up {' and '.join(reasons)}...")
            trigger_jellyfin_library_scan(jellyfin_url, api_key)
        else:
            logger.info("No new trailers or migrations; skipping Jellyfin library scan.")


if __name__ == "__main__":
    main()
