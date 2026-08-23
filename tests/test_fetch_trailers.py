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
    get_trailer_sources,
    create_trailer_filter,
    build_ydl_opts,
    load_config,
    get_jellyfin_movies,
    trigger_jellyfin_refresh,
    process_movie,
)


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
        # Movies containing 'poster' or 'cover' in title must be valid
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
        expected = [
            "https://www.youtube.com/watch?v=YoHD9XEInc0",
            "https://youtu.be/dummy123",
            "ytsearch5:Inception 2010 official trailer",
            "ytsearch5:Inception 2010"
        ]
        self.assertEqual(sources, expected)

    def test_sources_with_year_in_title(self):
        movie = {
            "Name": "Robot Riot (2020)",
            "ProductionYear": 2020,
            "RemoteTrailers": []
        }
        sources = get_trailer_sources(movie)
        expected = [
            "ytsearch5:Robot Riot 2020 official trailer",
            "ytsearch5:Robot Riot 2020"
        ]
        self.assertEqual(sources, expected)

    def test_sources_without_year_and_premiere_date(self):
        movie = {
            "Name": "Gladiator II",
            "PremiereDate": "2024-11-15T00:00:00.0000000Z",
            "RemoteTrailers": []
        }
        sources = get_trailer_sources(movie)
        expected = [
            "ytsearch5:Gladiator II 2024 official trailer",
            "ytsearch5:Gladiator II 2024"
        ]
        self.assertEqual(sources, expected)

    def test_sources_no_year_at_all(self):
        movie = {
            "Name": "Unknown Mystery",
            "RemoteTrailers": []
        }
        sources = get_trailer_sources(movie)
        expected = [
            "ytsearch5:Unknown Mystery official trailer",
            "ytsearch5:Unknown Mystery"
        ]
        self.assertEqual(sources, expected)


class TestTrailerFilter(unittest.TestCase):
    def test_filter_incomplete_returns_none(self):
        # yt-dlp calls filter with incomplete=True before full metadata is fetched
        filter_fn = create_trailer_filter("Inception", movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"title": None, "duration": None}, incomplete=True)
        self.assertIsNone(reason)

    def test_duration_over_5_minutes(self):
        filter_fn = create_trailer_filter("Inception", movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 305, "title": "Inception Official Trailer"}, incomplete=False)
        self.assertIn("Duration > 5min", reason)

    def test_duration_too_long_for_short_movie(self):
        # Short film of 300 seconds (5 min) - trailer of 200s is >= 60% of movie
        filter_fn = create_trailer_filter("Short Movie", movie_duration_sec=300, is_search=True)
        reason = filter_fn({"duration": 200, "title": "Short Movie Official Trailer"}, incomplete=False)
        self.assertIn("Too long compared to movie", reason)

    def test_search_filter_matches_title_and_keyword(self):
        filter_fn = create_trailer_filter("Inception", movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Inception (2010) Official Trailer #1"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_matches_german_keyword(self):
        filter_fn = create_trailer_filter("Inception", movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Inception - Deutscher Vorschau Trailer"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_handles_punctuation(self):
        # Title has colon, YouTube title has hyphen
        filter_fn = create_trailer_filter("Dune: Part Two", movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 180, "title": "Dune - Part Two | Official Trailer"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_handles_seeing_heaven(self):
        filter_fn = create_trailer_filter("Seeing Heaven", movie_duration_sec=6360, is_search=True)
        reason = filter_fn({"duration": 182, "title": "Seeing Heaven Trailer - QC Cinema"}, incomplete=False)
        self.assertIsNone(reason)

    def test_search_filter_rejects_missing_keyword(self):
        filter_fn = create_trailer_filter("Inception", movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Inception Full Soundtrack OST"}, incomplete=False)
        self.assertIn("Rejected.", reason)

    def test_search_filter_rejects_wrong_title(self):
        filter_fn = create_trailer_filter("Inception", movie_duration_sec=7200, is_search=True)
        reason = filter_fn({"duration": 150, "title": "Interstellar Official Trailer"}, incomplete=False)
        self.assertIn("Rejected.", reason)

    def test_remote_url_filter_accepts_valid_duration_regardless_of_keywords(self):
        # When is_search=False (direct remote trailer URL), it doesn't need cross-check keyword validation
        filter_fn = create_trailer_filter("Inception", movie_duration_sec=7200, is_search=False)
        reason = filter_fn({"duration": 150, "title": "Custom Remote Upload"}, incomplete=False)
        self.assertIsNone(reason)


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
        # Verify sorted by path
        self.assertEqual(movies[0]["Name"], "A")
        self.assertEqual(movies[1]["Name"], "Z")

    @patch("requests.post")
    def test_trigger_jellyfin_refresh(self, mock_post):
        mock_post.return_value.raise_for_status.return_value = None
        success = trigger_jellyfin_refresh("http://localhost:8096", "key", "item123")
        self.assertTrue(success)
        mock_post.assert_called_once()


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
        
        args = MagicMock(dry_run=True, sync=False, rename_original=False)
        state = {'last_dir': None}

        process_movie(movie, args, state, self.config)
        expected_trailer = os.path.join(self.test_dir, "Inception (2010)-trailer.mp4")
        self.assertFalse(os.path.exists(expected_trailer))

    def test_process_movie_skip_if_trailer_exists(self):
        movie_path = os.path.join(self.test_dir, "Inception (2010).mkv")
        trailer_path = os.path.join(self.test_dir, "Inception (2010)-trailer.mp4")
        with open(movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))
        with open(trailer_path, "wb") as f:
            f.write(b"trailer_content")

        movie = {
            "Id": "item123",
            "Name": "Inception",
            "ProductionYear": 2010,
            "Path": "/media/Movies/Inception (2010).mkv",
            "LocalTrailerCount": 1,
            "RemoteTrailers": []
        }
        
        args = MagicMock(dry_run=False, sync=False, rename_original=False)
        state = {'last_dir': None}

        with patch("jellyfin_trailer_fetcher.fetch_trailers.get_trailer_sources") as mock_sources:
            process_movie(movie, args, state, self.config)
            mock_sources.assert_not_called()

    @patch("jellyfin_trailer_fetcher.fetch_trailers.yt_dlp.YoutubeDL")
    @patch("jellyfin_trailer_fetcher.fetch_trailers.trigger_jellyfin_refresh")
    def test_process_movie_successful_download_and_sync(self, mock_refresh, mock_ydl_class):
        movie_path = os.path.join(self.test_dir, "Matrix (1999).mkv")
        with open(movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))

        movie = {
            "Id": "item456",
            "Name": "The Matrix",
            "ProductionYear": 1999,
            "Path": "/media/Movies/Matrix (1999).mkv",
            "LocalTrailerCount": 0,
            "RemoteTrailers": []
        }

        # Mock YoutubeDL instance behavior to simulate creating a downloaded temp file in outtmpl dir
        def fake_download(urls):
            opts = mock_ydl_class.call_args[0][0]
            outtmpl = opts['outtmpl']
            target_file = outtmpl.replace("%(ext)s", "mp4")
            with open(target_file, "wb") as f:
                f.write(b"mp4_trailer_bytes")
            return 0

        mock_ydl_instance = MagicMock()
        mock_ydl_instance.download.side_effect = fake_download
        mock_ydl_class.return_value.__enter__.return_value = mock_ydl_instance

        args = MagicMock(dry_run=False, sync=True, rename_original=False)
        state = {'last_dir': None}

        process_movie(movie, args, state, self.config)

        expected_trailer = os.path.join(self.test_dir, "The Matrix (1999)-trailer.mp4")
        self.assertTrue(os.path.exists(expected_trailer))
        with open(expected_trailer, "rb") as f:
            self.assertEqual(f.read(), b"mp4_trailer_bytes")

        mock_refresh.assert_called_once_with("http://mock-jellyfin:8096", "mock-key", "item456")

    @patch("jellyfin_trailer_fetcher.fetch_trailers.yt_dlp.YoutubeDL")
    def test_process_movie_fallback_from_private_remote_to_search(self, mock_ydl_class):
        movie_path = os.path.join(self.test_dir, "Robot Riot (2020).mkv")
        with open(movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))

        movie = {
            "Id": "item789",
            "Name": "Robot Riot",
            "ProductionYear": 2020,
            "Path": "/media/Movies/Robot Riot (2020).mkv",
            "LocalTrailerCount": 0,
            "RemoteTrailers": [{"Url": "https://www.youtube.com/watch?v=private_id"}]
        }

        # First call (private remote URL) raises exception; second call (search query) succeeds
        mock_ydl_instance = MagicMock()
        def fake_download(urls):
            url = urls[0]
            if "private_id" in url:
                raise Exception("ERROR: [youtube] private_id: Video unavailable. This video is private")
            # Search query succeeds
            opts = mock_ydl_class.call_args[0][0]
            # Ensure cookies are still present in options on fallback search!
            self.assertEqual(opts.get('cookiesfrombrowser'), ("firefox",))
            outtmpl = opts['outtmpl']
            target_file = outtmpl.replace("%(ext)s", "mp4")
            with open(target_file, "wb") as f:
                f.write(b"robot_riot_trailer_bytes")
            return 0

        mock_ydl_instance.download.side_effect = fake_download
        mock_ydl_class.return_value.__enter__.return_value = mock_ydl_instance

        args = MagicMock(dry_run=False, sync=False, rename_original=False)
        state = {'last_dir': None}

        process_movie(movie, args, state, self.config)

        expected_trailer = os.path.join(self.test_dir, "Robot Riot (2020)-trailer.mp4")
        self.assertTrue(os.path.exists(expected_trailer))

    def test_process_movie_rename_original(self):
        old_movie_path = os.path.join(self.test_dir, "Matrix.1999.1080p.mkv")
        with open(old_movie_path, "wb") as f:
            f.write(b"x" * (1024 * 1024 + 10))

        movie = {
            "Id": "item456",
            "Name": "The Matrix",
            "ProductionYear": 1999,
            "Path": "/media/Movies/Matrix.1999.1080p.mkv",
            "LocalTrailerCount": 0,
            "RemoteTrailers": []
        }

        args = MagicMock(dry_run=False, sync=False, rename_original=True)
        state = {'last_dir': None}

        with patch("jellyfin_trailer_fetcher.fetch_trailers.get_trailer_sources", return_value=[]):
            process_movie(movie, args, state, self.config)

        renamed_path = os.path.join(self.test_dir, "The Matrix (1999).mkv")
        self.assertTrue(os.path.exists(renamed_path))
        self.assertFalse(os.path.exists(old_movie_path))


if __name__ == "__main__":
    unittest.main()
