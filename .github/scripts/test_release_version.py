from pathlib import Path
import io
import json
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

from release_version import latest_stable_version, release_identity


class ReleaseVersionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.project = Path(self.temporary.name) / "App.csproj"
        self.project.write_text("<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>", encoding="utf-8")

    def identity(self, branch="main", number="184", attempt="1", run_id="1234567890", sha="a" * 40, latest_stable="2.0.0"):
        return release_identity(self.project, branch, number, attempt, run_id, sha, latest_stable)

    def test_beta_is_next_patch_and_all_assets_share_identity(self):
        result = self.identity()
        self.assertEqual("2.0.1-beta.184.1", result["version"])
        self.assertEqual("v" + result["version"], result["tag"])
        self.assertEqual("2.0.184.1", result["windows_version"])
        self.assertTrue(result["informational_version"].startswith(result["version"] + "+build.1234567890.1.sha."))
        self.assertEqual("true", result["prerelease"])

    def test_new_pushes_and_reruns_never_reuse_beta_identity(self):
        identities = [self.identity(number=str(number), attempt=str(attempt)) for number in (184, 185) for attempt in (1, 2)]
        for key in ("version", "tag", "windows_version"):
            self.assertEqual(4, len({value[key] for value in identities}))
        self.assertEqual(self.identity(), self.identity())  # matrix jobs/partial retries are deterministic

    def test_stable_tag_keeps_project_version(self):
        result = self.identity(branch="v2.0.0")
        self.assertEqual("2.0.0", result["version"])
        self.assertEqual("2.0.0.0", result["windows_version"])
        self.assertEqual("false", result["prerelease"])

    def test_preparing_stable_keeps_beta_core_until_stable_is_published(self):
        before = self.identity()
        self.project.write_text("<Project><PropertyGroup><Version>2.0.1</Version></PropertyGroup></Project>", encoding="utf-8")
        prepared = self.identity(number="185")  # main's version bump, stable is still 2.0.0
        self.assertEqual("2.0.1-beta.184.1", before["version"])
        self.assertEqual("2.0.1-beta.185.1", prepared["version"])
        self.assertEqual("2.0.1", self.identity(branch="v2.0.1")["version"])
        after = self.identity(number="186", latest_stable="2.0.1")
        self.assertEqual("2.0.2-beta.186.1", after["version"])

    def test_unreleased_project_and_first_release_use_project_core(self):
        self.assertEqual("2.0.0-beta.184.1", self.identity(latest_stable="1.9.3")["version"])
        self.assertEqual("2.0.0-beta.184.1", self.identity(latest_stable=None)["version"])

    def test_stale_project_or_invalid_stable_version_is_rejected(self):
        for version in ("2.0.1", "invalid", "2.0.0-beta.1", "2.00.0"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                self.identity(latest_stable=version)

    def test_latest_stable_api_only_accepts_published_numeric_stable_tags(self):
        payload = {"tag_name": "v2.0.0", "draft": False, "prerelease": False}
        with patch("release_version.urlopen", return_value=io.BytesIO(json.dumps(payload).encode())) as request:
            self.assertEqual("2.0.0", latest_stable_version("rainaku/V-Notch", "fixture-token"))
            self.assertEqual("https://api.github.com/repos/rainaku/V-Notch/releases/latest", request.call_args.args[0].full_url)
        for change in ({"draft": True}, {"prerelease": True}, {"tag_name": "v2.0.1-beta.1"}, {"tag_name": "nightly"}):
            with self.subTest(change=change), patch("release_version.urlopen", return_value=io.BytesIO(json.dumps(payload | change).encode())), self.assertRaises(ValueError):
                latest_stable_version("rainaku/V-Notch", "fixture-token")

    def test_only_api_404_means_no_stable_other_errors_fail_closed(self):
        for code in (404, 403, 429, 500):
            error = HTTPError("https://api.github.com/fixture", code, "fixture error", {}, None)
            with self.subTest(code=code), patch("release_version.urlopen", side_effect=error):
                if code == 404:
                    self.assertIsNone(latest_stable_version("rainaku/V-Notch", "fixture-token"))
                else:
                    with self.assertRaises(HTTPError):
                        latest_stable_version("rainaku/V-Notch", "fixture-token")

    def test_ref_sha_and_number_errors_fail_closed(self):
        for values in ({"branch": "feature"}, {"branch": "v2.0.1"}, {"sha": "short"},
                       {"number": "0"}, {"attempt": "01"}, {"run_id": "text"},
                       {"number": "65535"}, {"attempt": "65535"}):
            with self.subTest(values=values), self.assertRaises(ValueError):
                self.identity(**values)


if __name__ == "__main__":
    unittest.main()
