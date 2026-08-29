import os
import sys
import unittest
import tempfile
import shutil
from unittest.mock import patch, MagicMock

# Ensure src is in sys.path
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "src")))

from jellyfin_trailer_fetcher.fetch_trailers import (
    translate_path,
    sanitize_filename,
    is_valid_media_file,
    is_non_latin,
    clean_media_title,
    extract_main_title,
    resolve_movie_titles,
    get_trailer_sources,
    create_trailer_filter,
    build_ydl_opts,
    load_config,
    get_jellyfin_movies,
    trigger_jellyfin_library_scan,
    process_movie,
    migrate_movie_to_own_folder,
    main,
)
import jellyfin_trailer_fetcher.fetch_trailers as fetch_trailers_module


class TestPathTranslation(unittest.TestCase):
    def test_translate_path_success(self):
        mappings = {
            "/media/Movies/": "/Volumes/Media/Movies/",
            "/media/Anime/": "/Volumes/Media/Anime/"
        }
        res = translate_path("/media/Movies/Inception (2010)/movie.mkv", mappings)
        self.assertEqual(res, "/Volumes/Media/Movies/Inception (2010)/movie.mkv")

    def test_translate_path_without_trailing_slashes(self):
        mappings = {
            "/media/Movies": "/Volumes/Media/Movies"
        }
        res = translate_path("/media/Movies/Inception (2010)/movie.mkv", mappings)
        self.assertEqual(res, "/Volumes/Media/Movies/Inception (2010)/movie.mkv")

    def test_translate_path_no_match(self):
        mappings = {"/media/Movies/": "/Volumes/Media/Movies/"}
        res = translate_path("/other/Path/movie.mkv", mappings)
        self.assertIsNone(res)

    def test_translate_path_empty(self):
        self.assertIsNone(translate_path("", {"/a": "/b"}))
        self.assertIsNone(translate_path(None, {"/a": "/b"}))
        self.assertIsNone(translate_path("/a/file.mkv", {}))


class TestSanitizeFilename(unittest.TestCase):
    def test_sanitize_clean(self):
        self.assertEqual(sanitize_filename("The Matrix (1999)"), "The Matrix (1999)")

    def test_sanitize_special_characters(self):
        self.assertEqual(
            sanitize_filename('Alien: Romulus / "Special" * <Edition>? | [2024]'),
            "Alien Romulus  Special  Edition  [2024]"
        )

    def test_sanitize_empty(self):
        self.assertEqual(sanitize_filename("???"), "Unknown_Movie")
        self.assertEqual(sanitize_filename(""), "Unknown_Movie")


class TestTitleResolutionAndExtraction(unittest.TestCase):
    def test_is_non_latin(self):
        self.assertTrue(is_non_latin("ギヴン うらがわの存在"))
        self.assertTrue(is_non_latin("ゴキブリたちの黄昏"))
        self.assertTrue(is_non_latin("嫦娥奔月"))
        self.assertFalse(is_non_latin("Given - On the other hand"))
        self.assertFalse(is_non_latin("DAKAICHI"))
        self.assertFalse(is_non_latin("Gokiburi-tachi no Tasogare"))

    def test_clean_media_title(self):
        t1, y1 = clean_media_title("01 Vampire Hunter D - Animation 1985 Eng Jpn Multi Subs 720p [H264-mp4]")
        self.assertEqual(t1, "Vampire Hunter D")
        self.assertEqual(y1, "1985")

        t2, y2 = clean_media_title("02 Vampire Hunter D Bloodlust - Animation 2000 Eng Jpn Multi Subs 720p [H264-mp4]")
        self.assertEqual(t2, "Vampire Hunter D Bloodlust")
        self.assertEqual(y2, "2000")

        t3, y3 = clean_media_title("DAKAICHI - Im being harassed by the sexiest man of the year (2024)")
        self.assertEqual(t3, "DAKAICHI - Im being harassed by the sexiest man of the year")
        self.assertEqual(y3, "2024")

    def test_clean_media_title_strips_eac3_and_spelled_out_language(self):
        # "ac3" alone doesn't match inside "eac3" due to the word boundary, and spelled-
        # out language names (as opposed to 3-letter codes like "chi") weren't stripped
        # at all - both left an orphaned, now-empty "( )" behind too.
        t, y = clean_media_title("Chang An ( EAC3 ) CHINESE")
        self.assertEqual(t, "Chang An")
        self.assertIsNone(y)

    def test_extract_main_title(self):
        clean, main, first_w = extract_main_title("DAKAICHI - Im being harassed by the sexiest man of the year (2024)")
        self.assertEqual(clean, "DAKAICHI - Im being harassed by the sexiest man of the year")
        self.assertEqual(main, "DAKAICHI")
        self.assertEqual(first_w, "DAKAICHI")

        clean, main, first_w = extract_main_title("Given: On the other hand")
        self.assertEqual(clean, "Given: On the other hand")
        self.assertEqual(main, "Given")
        self.assertEqual(first_w, "Given")

        clean, main, first_w = extract_main_title("ギヴン うらがわの存在")
        self.assertEqual(clean, "ギヴン うらがわの存在")
        self.assertEqual(first_w, "ギヴン")

    def test_resolve_movie_titles_scene_release(self):
        movie = {
            "Name": "01 Vampire Hunter D - Animation 1985 Eng Jpn Multi Subs 720p [H264-mp4]",
            "OriginalTitle": ""
        }
        preferred, variants = resolve_movie_titles(movie, "/path/01 Vampire Hunter D.mp4")
        self.assertEqual(preferred, "Vampire Hunter D")
        self.assertIn("Vampire Hunter D", variants)

    def test_resolve_movie_titles_latin_preservation(self):
        movie = {
            "Name": "ギヴン うらがわの存在",
            "OriginalTitle": ""
        }
        preferred, variants = resolve_movie_titles(movie, "/path/Given - On the other hand (2021).mp4")
        self.assertEqual(preferred, "Given - On the other hand")
        self.assertIn("Given - On the other hand", variants)
        self.assertIn("ギヴン うらがわの存在", variants)

    def test_resolve_movie_titles_latin_original_title(self):
        movie = {
            "Name": "ゴキブリたちの黄昏",
            "OriginalTitle": "Gokiburi-tachi no Tasogare"
        }
        preferred, variants = resolve_movie_titles(movie, "/path/movie.mkv")
        self.assertEqual(preferred, "Gokiburi-tachi no Tasogare")

    def test_resolve_movie_titles_prefers_filename_over_unrelated_metadata_name(self):
        # Jellyfin's "Name" metadata can be wrong in a way cleaning can't fix (bad
        # provider match, unrelated to any known scene-release/technical noise word).
        # When the user's own filename shares no words at all with it, trust the
        # filename over the metadata.
        movie = {
            "Name": "Release Group XJ99 Print",
            "OriginalTitle": "",
            "ProductionYear": 2023,
        }
        preferred, variants = resolve_movie_titles(
            movie, "/path/Chang'e and the Jade Rabbit's Mid-Autumn Adventure (2023).mkv"
        )
        self.assertEqual(preferred, "Chang'e and the Jade Rabbit's Mid-Autumn Adventure")
        # The original (possibly still-correct-for-search) metadata name is kept as a
        # fallback search variant, just no longer the primary/preferred title.
        self.assertIn("Release Group XJ99 Print", variants)

    def test_resolve_movie_titles_cleans_codec_and_language_junk_directly(self):
        # A container title tag full of leftover technical junk - audio codec (EAC3,
        # not caught by the old "ac3"-only pattern due to the word boundary) and a
        # spelled-out language name - should be recovered by cleaning alone, without
        # needing to fall back to the filename at all.
        movie = {"Name": "Chang An ( EAC3 ) CHINESE", "OriginalTitle": "", "ProductionYear": 2012}
        preferred, variants = resolve_movie_titles(movie, "/path/Chang An (2023).mkv")
        self.assertEqual(preferred, "Chang An")

    def test_resolve_movie_titles_two_word_stopword_title_still_overrides(self):
        # A genuine two-word filename title where one word is a filler/stopword (e.g.
        # "The Room") must still count as "substantial" for the override, even though
        # only one word survives stopword-filtering for the overlap comparison.
        movie = {"Name": "Release Group XJ99 Print", "OriginalTitle": "", "ProductionYear": 2003}
        preferred, variants = resolve_movie_titles(movie, "/path/The Room (2003).mkv")
        self.assertEqual(preferred, "The Room")

    def test_resolve_movie_titles_keeps_metadata_name_when_generic_filename(self):
        # A generic/uninformative filename (e.g. a bare "movie.mkv") must not override
        # a legitimate metadata title just because the words happen not to overlap.
        movie = {"Name": "Interstellar", "OriginalTitle": ""}
        preferred, variants = resolve_movie_titles(movie, "/path/movie.mkv")
        self.assertEqual(preferred, "Interstellar")


class TestMediaValidation(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.test_dir)

    def test_valid_video_file(self):
        for ext in [".mp4", ".mkv", ".avi", ".mov", ".m4v", ".ts", ".iso"]:
            movie_file = os.path.join(self.test_dir, f"Movie (2020){ext}")
            with open(movie_file, "wb") as f:
                f.write(b"x" * (1024 * 1024 + 10))
            valid, reason = is_valid_media_file(movie_file)
            self.assertTrue(valid, f"Failed for extension {ext}: {reason}")
            self.assertIsNone(reason)

    def test_movie_with_poster_or_cover_in_title(self):
        for name in ["Poster Boy! (2015).avi", "Cover Girl (1944).mkv", "The Poster (2020).mp4"]:
            movie_file = os.path.join(self.test_dir, name)
            with open(movie_file, "wb") as f:
                f.write(b"x" * (1024 * 1024 + 10))
            valid, reason = is_valid_media_file(movie_file)
            self.assertTrue(valid, f"Failed for '{name}': {reason}")
            self.assertIsNone(reason)

    def test_nonexistent_file(self):
        valid, reason = is_valid_media_file(os.path.join(self.test_dir, "ghost.mkv"))
        self.assertFalse(valid)
        self.assertIn("does not exist", reason)

    def test_directory_instead_of_file(self):
        sub_dir = os.path.join(self.test_dir, "Movie Folder")
        os.makedirs(sub_dir)
        valid, reason = is_valid_media_file(sub_dir)
        self.assertFalse(valid)
        self.assertIn("not a regular file", reason)

    def test_file_size_too_small(self):
        small_file = os.path.join(self.test_dir, "corrupted.mkv")
        with open(small_file, "wb") as f:
            f.write(b"tiny")
        valid, reason = is_valid_media_file(small_file)
        self.assertFalse(valid)
        self.assertIn("File size too small", reason)

    def test_non_video_extension(self):
        for ext in [".nfo", ".txt", ".jpg", ".png", ".srt"]:
            bad_file = os.path.join(self.test_dir, f"poster{ext}")
            with open(bad_file, "wb") as f:
                f.write(b"x" * (1024 * 1024 + 10))
            valid, reason = is_valid_media_file(bad_file)
            self.assertFalse(valid, f"Expected non-video rejection for {ext}")
            self.assertIn("Not a video file", reason)

    def test_ignored_trailer_file(self):
        for name in ["Movie (2020)-trailer.mp4", "trailer.mkv", "Movie_trailer.avi"]:
            trailer_file = os.path.join(self.test_dir, name)
            with open(trailer_file, "wb") as f:
                f.write(b"x" * (1024 * 1024 + 10))
            valid, reason = is_valid_media_file(trailer_file)
            self.assertFalse(valid, f"Expected rejection for trailer '{name}'")
            self.assertIn("already a trailer", reason)

    def test_ignored_sample_file(self):
        for name in ["Movie.sample.mkv", "sample.mkv", "Movie-sample.avi"]:
            sample_file = os.path.join(self.test_dir, name)
            with open(sample_file, "wb") as f:
                f.write(b"x" * (1024 * 1024 + 10))
            valid, reason = is_valid_media_file(sample_file)
            self.assertFalse(valid, f"Expected rejection for sample '{name}'")
            self.assertIn("sample clip", reason)

    def test_ignored_extras_directory(self):
        extras_dir = os.path.join(self.test_dir, "extras")
        os.makedirs(extras_dir)
        extra_file = os.path.join(extras_dir, "clip.mkv")
        with open(extra_file, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))
        valid, reason = is_valid_media_file(extra_file)
        self.assertFalse(valid)
        self.assertIn("extras directory", reason)


class TestTrailerSources(unittest.TestCase):
    def test_sources_with_remote_trailers_and_year(self):
        movie = {
            "Name": "Inception",
            "ProductionYear": 2010,
            "RemoteTrailers": [
                {"Url": "https://www.youtube.com/watch?v=YoHD9XEInc0"},
                "https://youtu.be/dummy123",
                {"Url": "https://vimeo.com/123456"}  # Non-youtube should be ignored
            ]
        }
        sources = get_trailer_sources(movie)
        self.assertIn("https://www.youtube.com/watch?v=YoHD9XEInc0", sources)
        self.assertIn("https://youtu.be/dummy123", sources)
        self.assertIn("ytsearch5:Inception 2010 official trailer", sources)
        self.assertIn("ytsearch5:Inception official trailer", sources)
        self.assertIn("ytsearch5:Inception trailer", sources)

    def test_sources_with_subtitle_separation(self):
        movie = {
            "Name": "DAKAICHI - Im being harassed by the sexiest man of the year",
            "ProductionYear": 2024,
            "RemoteTrailers": []
        }
        sources = get_trailer_sources(movie)
        self.assertIn("ytsearch5:DAKAICHI - Im being harassed by the sexiest man of the year 2024 official trailer", sources)
        self.assertIn("ytsearch5:DAKAICHI 2024 official trailer", sources)
        self.assertIn("ytsearch5:DAKAICHI official trailer", sources)
        self.assertIn("ytsearch5:DAKAICHI trailer", sources)
        self.assertIn("ytsearch5:DAKAICHI 2024", sources)
        self.assertIn("ytsearch5:DAKAICHI", sources)

    def test_sources_with_japanese_and_latin_titles(self):
        movie = {
            "Name": "ギヴン うらがわの存在",
            "ProductionYear": 2021,
            "RemoteTrailers": []
        }
        sources = get_trailer_sources(movie, "/media/Given - On the other hand.mp4")
        self.assertIn("ytsearch5:Given - On the other hand 2021 official trailer", sources)
        self.assertIn("ytsearch5:Given 2021 official trailer", sources)
        self.assertIn("ytsearch5:Given official trailer", sources)
        self.assertIn("ytsearch5:ギヴン うらがわの存在 2021 予告", sources)
        self.assertIn("ytsearch5:ギヴン うらがわの存在 PV", sources)


class TestTrailerFilter(unittest.TestCase):
    def test_filter_incomplete_returns_none(self):
        filter_fn = create_trailer_filter(["Inception"], movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"title": None, "duration": None}, incomplete=True)
        self.assertIsNone(reason)

    def test_duration_over_5_minutes(self):
        filter_fn = create_trailer_filter(["Inception"], movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 305, "title": "Inception Official Trailer"}, incomplete=False)
        self.assertIn("Duration > 5min", reason)

    def test_duration_too_long_for_short_movie(self):
        filter_fn = create_trailer_filter(["Short Movie"], movie_duration_sec=300, is_search=True)
        reason = filter_fn({"duration": 200, "title": "Short Movie Official Trailer"}, incomplete=False)
        self.assertIn("Too long compared to movie", reason)

    def test_search_filter_matches_main_title_without_subtitle(self):
        filter_fn = create_trailer_filter(
            ["DAKAICHI - Im being harassed by the sexiest man of the year", "DAKAICHI"],
            movie_duration_sec=5400,
            is_search=True
        )
        reason = filter_fn({"duration": 120, "title": "DAKAICHI Spain Arc Trailer"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_matches_japanese_pv_and_latin_titles(self):
        filter_fn = create_trailer_filter(
            ["Given - On the other hand", "Given", "ギヴン うらがわの存在"],
            movie_duration_sec=5400,
            is_search=True
        )
        # Latin Crunchyroll trailer
        self.assertIsNone(filter_fn({"duration": 98, "title": "given - TRAILER OFFICIEL | Crunchyroll"}, incomplete=False))
        # Japanese official PV
        self.assertIsNone(filter_fn({"duration": 95, "title": "TVアニメ「ギヴン」PV"}, incomplete=False))

    def test_search_filter_matches_change_jade_rabbit(self):
        filter_fn = create_trailer_filter(
            ["Chang'e and the Jade Rabbit's Mid-Autumn Adventure", "Chang'e and the Jade Rabbit"],
            movie_duration_sec=1800,
            is_search=True
        )
        reason = filter_fn({"duration": 148, "title": "[ENG SUB] Chang'e and the Jade Rabbit's Mid-Autumn Adventure"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_rejects_missing_keyword(self):
        filter_fn = create_trailer_filter(["Inception"], movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Inception Full Soundtrack OST"}, incomplete=False)
        self.assertIn("Rejected.", reason)

    def test_search_filter_rejects_wrong_title(self):
        filter_fn = create_trailer_filter(["Inception"], movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Interstellar Official Trailer"}, incomplete=False)
        self.assertIn("Rejected.", reason)

    def test_search_filter_allows_year_right_after_title(self):
        # A release year immediately after the title is a normal trailer title pattern
        # and must not be confused with a franchise/sequel number.
        filter_fn = create_trailer_filter(["It"], movie_duration_sec=6000, is_search=True)
        reason = filter_fn({"duration": 140, "title": "IT (2017) Official Trailer"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_rejects_wrong_sequel_installment(self):
        # Searching for "Iron Man" must not accept a result for "Iron Man 2".
        filter_fn = create_trailer_filter(["Iron Man"], movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Iron Man 2 Official Trailer"}, incomplete=False)
        self.assertIn("Rejected.", reason)

    def test_search_filter_matches_correct_sequel_installment(self):
        # Searching for "Iron Man 2" itself must still match its own trailer.
        filter_fn = create_trailer_filter(["Iron Man 2"], movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Iron Man 2 Official Trailer"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_rejects_substring_within_longer_word(self):
        # "Cars" must not match a video whose title merely contains "cars" as a
        # substring of an unrelated word like "Scars".
        filter_fn = create_trailer_filter(["Cars"], movie_duration_sec=6000, is_search=True)
        reason = filter_fn({"duration": 140, "title": "Scars Official Trailer"}, incomplete=False)
        self.assertIn("Rejected.", reason)


class TestConfigAndYtdlpOpts(unittest.TestCase):
    @patch.dict(os.environ, {
        "JELLYFIN_URL": "http://localhost:8096/",
        "API_KEY": "testkey123",
        "PATH_MAPPINGS": '{"/nas/": "/local/"}',
        "COOKIE_BROWSER": "firefox"
    }, clear=True)
    def test_load_config_with_path_mappings(self):
        url, key, mappings, cookie_browser = load_config(load_env=False)
        self.assertEqual(url, "http://localhost:8096")
        self.assertEqual(key, "testkey123")
        self.assertEqual(mappings, {"/nas/": "/local/"})
        self.assertEqual(cookie_browser, "firefox")

    @patch.dict(os.environ, {
        "JELLYFIN_URL": "http://localhost:8096",
        "API_KEY": "testkey123",
        "NAS_PATH_PREFIX": "/nas/",
        "MAC_PATH_PREFIX": "/local/",
        "COOKIE_BROWSER": "none"
    }, clear=True)
    def test_load_config_fallback_prefix(self):
        url, key, mappings, cookie_browser = load_config(load_env=False)
        self.assertEqual(url, "http://localhost:8096")
        self.assertEqual(key, "testkey123")
        self.assertEqual(mappings, {"/nas/": "/local/"})
        self.assertIsNone(cookie_browser)

    def test_build_ydl_opts(self):
        opts = build_ydl_opts("/tmp/test.%(ext)s", lambda x, **k: None, cookie_browser="firefox")
        self.assertEqual(opts['outtmpl'], "/tmp/test.%(ext)s")
        self.assertTrue(opts['geo_bypass'])
        self.assertEqual(opts['max_downloads'], 1)
        self.assertEqual(opts['cookiesfrombrowser'], ("firefox",))

    def test_build_ydl_opts_extractor_args_shape(self):
        # extractor_args must be the nested-dict shape yt-dlp's Python API expects
        # (traverse_obj(params, ('extractor_args', 'youtube', 'player_client'))),
        # not the "key=value" CLI string shape - the latter is silently ignored.
        opts = build_ydl_opts("/tmp/test.%(ext)s", lambda x, **k: None, cookie_browser="firefox")
        player_client = opts['extractor_args']['youtube']['player_client']
        # With cookies configured, android/ios are skipped by yt-dlp anyway (they don't
        # support cookies), so only 'web' should be requested to avoid noisy warnings.
        self.assertEqual(player_client, ['web'])

    def test_build_ydl_opts_extractor_args_no_cookies(self):
        # Without cookies, android/ios are useful unauthenticated fallbacks.
        opts = build_ydl_opts("/tmp/test.%(ext)s", lambda x, **k: None, cookie_browser=None)
        player_client = opts['extractor_args']['youtube']['player_client']
        self.assertEqual(player_client, ['web', 'android', 'ios'])


class TestJellyfinAPI(unittest.TestCase):
    @patch("requests.get")
    def test_get_jellyfin_movies_success(self, mock_get):
        mock_response = MagicMock()
        mock_response.json.return_value = {
            "Items": [
                {"Path": "/media/Movies/Z.mkv", "Name": "Z"},
                {"Path": "/media/Movies/A.mkv", "Name": "A"}
            ]
        }
        mock_response.raise_for_status.return_value = None
        mock_get.return_value = mock_response

        movies = get_jellyfin_movies("http://localhost:8096", "key")
        self.assertEqual(len(movies), 2)
        self.assertEqual(movies[0]["Name"], "A")
        self.assertEqual(movies[1]["Name"], "Z")

    @patch("requests.post")
    def test_trigger_jellyfin_library_scan(self, mock_post):
        mock_post.return_value.raise_for_status.return_value = None
        success = trigger_jellyfin_library_scan("http://localhost:8096", "key")
        self.assertTrue(success)
        mock_post.assert_called_once_with(
            "http://localhost:8096/Library/Refresh",
            headers={"Authorization": 'MediaBrowser Token="key"'},
            timeout=15,
        )


class TestMovieProcessing(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.mappings = {"/media/Movies/": f"{self.test_dir}/"}
        self.config = ("http://mock-jellyfin:8096", "mock-key", self.mappings, "firefox")

    def tearDown(self):
        shutil.rmtree(self.test_dir)

    def test_process_movie_dry_run(self):
        movie_path = os.path.join(self.test_dir, "Inception (2010).mkv")
        with open(movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))

        movie = {
            "Id": "item123",
            "Name": "Inception",
            "ProductionYear": 2010,
            "Path": "/media/Movies/Inception (2010).mkv",
            "LocalTrailerCount": 0,
            "RemoteTrailers": []
        }
        
        args = MagicMock(dry_run=True, sync=False, rename_original=False, migrate_to_folders=None)
        state = {'last_dir': None}

        process_movie(movie, args, state, self.config)
        expected_trailer = os.path.join(self.test_dir, "Inception (2010)-trailer.mp4")
        self.assertFalse(os.path.exists(expected_trailer))

    def test_process_movie_preserves_latin_filename_during_rename(self):
        movie_path = os.path.join(self.test_dir, "Given - On the other hand.mp4")
        with open(movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))

        movie = {
            "Id": "item123",
            "Name": "ギヴン うらがわの存在",  # Japanese Jellyfin name
            "ProductionYear": 2021,
            "Path": "/media/Movies/Given - On the other hand.mp4",
            "LocalTrailerCount": 0,
            "RemoteTrailers": []
        }

        args = MagicMock(dry_run=False, sync=False, rename_original=True, migrate_to_folders=None)
        state = {'last_dir': None}

        with patch("jellyfin_trailer_fetcher.fetch_trailers.get_trailer_sources", return_value=[]):
            process_movie(movie, args, state, self.config)

        # Renamed file should preserve Latin script
        expected_renamed = os.path.join(self.test_dir, "Given - On the other hand (2021).mp4")
        self.assertTrue(os.path.exists(expected_renamed))
        # Ensure Japanese characters were NOT used for filename
        japanese_path = os.path.join(self.test_dir, "ギヴン うらがわの存在 (2021).mp4")
        self.assertFalse(os.path.exists(japanese_path))


class TestMigrateToOwnFolder(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp()
        self.mappings = {"/media/Movies/": f"{self.test_dir}/"}
        self.config = ("http://mock-jellyfin:8096", "mock-key", self.mappings, "firefox")

    def tearDown(self):
        shutil.rmtree(self.test_dir)

    def _touch(self, name, size=1024 * 1024 + 10):
        p = os.path.join(self.test_dir, name)
        with open(p, "wb") as f:
            f.write(b"x" * size)
        return p

    def test_migrate_moves_movie_and_sidecars_out_of_shared_folder(self):
        # Simulate a flat folder holding two unrelated movies plus sidecar files.
        self._touch("Inception (2010).mkv")
        self._touch("Inception (2010).eng.srt", size=100)
        self._touch("Inception (2010)-trailer.mp4", size=100)
        self._touch("Interstellar (2014).mkv")  # unrelated sibling movie

        local_path = os.path.join(self.test_dir, "Inception (2010).mkv")
        new_path, moved = migrate_movie_to_own_folder(local_path)

        self.assertTrue(moved)
        expected_dir = os.path.join(self.test_dir, "Inception (2010)")
        self.assertEqual(new_path, os.path.join(expected_dir, "Inception (2010).mkv"))
        self.assertTrue(os.path.exists(os.path.join(expected_dir, "Inception (2010).mkv")))
        self.assertTrue(os.path.exists(os.path.join(expected_dir, "Inception (2010).eng.srt")))
        self.assertTrue(os.path.exists(os.path.join(expected_dir, "Inception (2010)-trailer.mp4")))
        # The unrelated sibling movie must be left untouched in the shared folder.
        self.assertTrue(os.path.exists(os.path.join(self.test_dir, "Interstellar (2014).mkv")))

    def test_migrate_uses_extra_stem_to_catch_differently_named_trailer(self):
        # The movie file keeps its original messy name (no --rename-original), but the
        # trailer was saved under the cleaned "safe_title" - migration must still catch it.
        self._touch("Inception.2010.WEB-DL.mkv")
        self._touch("Inception (2010)-trailer.mp4", size=100)

        local_path = os.path.join(self.test_dir, "Inception.2010.WEB-DL.mkv")
        new_path, moved = migrate_movie_to_own_folder(local_path, extra_stems=("Inception (2010)",))

        self.assertTrue(moved)
        expected_dir = os.path.join(self.test_dir, "Inception.2010.WEB-DL")
        self.assertTrue(os.path.exists(os.path.join(expected_dir, "Inception.2010.WEB-DL.mkv")))
        self.assertTrue(os.path.exists(os.path.join(expected_dir, "Inception (2010)-trailer.mp4")))

    def test_migrate_is_idempotent_when_already_in_own_folder(self):
        own_dir = os.path.join(self.test_dir, "Inception (2010)")
        os.makedirs(own_dir)
        movie_path = os.path.join(own_dir, "Inception (2010).mkv")
        with open(movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))

        new_path, moved = migrate_movie_to_own_folder(movie_path)
        self.assertFalse(moved)
        self.assertEqual(new_path, movie_path)

    def test_migrate_dry_run_does_not_touch_disk(self):
        self._touch("Inception (2010).mkv")
        local_path = os.path.join(self.test_dir, "Inception (2010).mkv")

        new_path, would_move = migrate_movie_to_own_folder(local_path, dry_run=True)

        self.assertTrue(would_move)
        self.assertEqual(new_path, local_path)
        self.assertTrue(os.path.exists(local_path))
        self.assertFalse(os.path.exists(os.path.join(self.test_dir, "Inception (2010)")))

    def test_process_movie_migrate_mode_trailers_only_touches_movies_with_trailer(self):
        # "trailers" mode must migrate a movie that already has a local trailer, but
        # leave an unrelated movie with no trailer flat/untouched.
        self._touch("Inception (2010).mkv")
        self._touch("Inception (2010)-trailer.mp4", size=100)
        self._touch("Interstellar (2014).mkv")

        movie_with_trailer = {
            "Id": "m1", "Name": "Inception", "ProductionYear": 2010,
            "Path": "/media/Movies/Inception (2010).mkv",
            "LocalTrailerCount": 1, "RemoteTrailers": []
        }
        movie_without_trailer = {
            "Id": "m2", "Name": "Interstellar", "ProductionYear": 2014,
            "Path": "/media/Movies/Interstellar (2014).mkv",
            "LocalTrailerCount": 0, "RemoteTrailers": []
        }

        args = MagicMock(dry_run=False, sync=False, rename_original=False, migrate_to_folders="trailers")
        state = {'last_dir': None}

        with patch("jellyfin_trailer_fetcher.fetch_trailers.get_trailer_sources", return_value=[]):
            process_movie(movie_with_trailer, args, state, self.config)
            process_movie(movie_without_trailer, args, state, self.config)

        self.assertTrue(os.path.exists(os.path.join(self.test_dir, "Inception (2010)", "Inception (2010).mkv")))
        self.assertTrue(os.path.exists(os.path.join(self.test_dir, "Inception (2010)", "Inception (2010)-trailer.mp4")))
        # No trailer -> stays exactly where it was, untouched.
        self.assertTrue(os.path.exists(os.path.join(self.test_dir, "Interstellar (2014).mkv")))
        self.assertEqual(state.get('migrated', 0), 1)

    def test_process_movie_migrate_mode_all_touches_every_movie(self):
        self._touch("Interstellar (2014).mkv")

        movie = {
            "Id": "m2", "Name": "Interstellar", "ProductionYear": 2014,
            "Path": "/media/Movies/Interstellar (2014).mkv",
            "LocalTrailerCount": 0, "RemoteTrailers": []
        }

        args = MagicMock(dry_run=False, sync=False, rename_original=False, migrate_to_folders="all")
        state = {'last_dir': None}

        with patch("jellyfin_trailer_fetcher.fetch_trailers.get_trailer_sources", return_value=[]):
            process_movie(movie, args, state, self.config)

        self.assertTrue(os.path.exists(os.path.join(self.test_dir, "Interstellar (2014)", "Interstellar (2014).mkv")))
        self.assertEqual(state.get('migrated', 0), 1)


class TestMainTriggersSyncOnMigrationAlone(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.test_dir)

    def test_sync_fires_when_only_migration_happened_no_downloads(self):
        # A run that only moves already-trailered movies into their own folder (no new
        # trailer downloaded this run) must still trigger a library scan under --sync -
        # Jellyfin needs to see the moved paths too, not just new trailer files.
        movie_path = os.path.join(self.test_dir, "Inception (2010).mkv")
        with open(movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))
        trailer_path = os.path.join(self.test_dir, "Inception (2010)-trailer.mp4")
        with open(trailer_path, "wb") as f:
            f.write(b"x" * 100)

        movies = [{
            "Id": "m1", "Name": "Inception", "ProductionYear": 2010,
            "Path": "/media/Movies/Inception (2010).mkv",
            "LocalTrailerCount": 1, "RemoteTrailers": []
        }]
        config = ("http://mock-jellyfin:8096", "mock-key", {"/media/Movies/": f"{self.test_dir}/"}, None)

        with patch.object(fetch_trailers_module, "get_jellyfin_movies", return_value=movies), \
             patch.object(fetch_trailers_module, "load_config", return_value=config), \
             patch.object(fetch_trailers_module, "trigger_jellyfin_library_scan") as mock_scan:
            main(["--sync", "--migrate-to-folders", "trailers"])

        mock_scan.assert_called_once_with("http://mock-jellyfin:8096", "mock-key")
        self.assertTrue(os.path.exists(os.path.join(self.test_dir, "Inception (2010)", "Inception (2010).mkv")))


if __name__ == "__main__":
    unittest.main()
