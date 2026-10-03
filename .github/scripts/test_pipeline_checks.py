import json
from pathlib import Path
import tempfile
import subprocess
import unittest

from check_sarif import check
from ci_scope import needs_windows, release_eligible, scope


class PipelineChecks(unittest.TestCase):
    def test_known_docs_can_skip_windows(self):
        self.assertFalse(needs_windows(["README.md", "docs/setup.md"]))

    def test_packaged_docs_and_unknown_changes_require_windows(self):
        for paths in ([], ["TERMS_OF_SERVICE.md"], ["THIRD_PARTY_NOTICES.md"], ["README.md", "V-Notch.csproj"], [".gitleaks.toml"]):
            with self.subTest(paths=paths):
                self.assertTrue(needs_windows(paths))

    def test_tags_and_new_branches_require_windows(self):
        self.assertTrue(scope("a" * 40, "b" * 40, "refs/tags/v2.0.0"))
        self.assertTrue(scope("0" * 40, "b" * 40, "refs/heads/main"))

    def test_actual_git_diff_cannot_hide_source_deletion_as_a_docs_rename(self):
        with tempfile.TemporaryDirectory(prefix="vnotch-scope-test-") as temporary:
            root = Path(temporary).resolve()
            self.assertTrue(root.is_relative_to(Path(tempfile.gettempdir()).resolve()))
            def git(*args):
                return subprocess.run(["git", *args], cwd=root, check=True, capture_output=True, text=True).stdout.strip()
            def commit():
                git("add", ".")
                git("-c", "user.name=Scope Test", "-c", "user.email=scope@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Scope test")
                return git("rev-parse", "HEAD")
            git("init", "--quiet")
            (root / "App.cs").write_text("class Program {}", encoding="utf-8")
            (root / "README.md").write_text("Documentation", encoding="utf-8")
            base = commit()
            (root / "README.md").write_text("Updated documentation", encoding="utf-8")
            docs = commit()
            self.assertFalse(scope(base, docs, "refs/heads/main", root))
            (root / "docs").mkdir()
            git("mv", "App.cs", "docs/App.md")
            renamed = commit()
            self.assertTrue(scope(docs, renamed, "refs/heads/main", root))

    def test_only_successful_validation_of_release_commit_is_eligible(self):
        job = {"name": "Windows validation", "head_sha": "a" * 40, "status": "completed", "conclusion": "success"}
        self.assertTrue(release_eligible({"jobs": [job]}, "a" * 40))
        for status in ("skipped", "failure", "cancelled"):
            self.assertFalse(release_eligible({"jobs": [{**job, "conclusion": status}]}, "a" * 40))
        with self.assertRaises(ValueError):
            release_eligible({"jobs": [job]}, "b" * 40)
        with self.assertRaises(ValueError):
            release_eligible({"jobs": []}, "a" * 40)

    def test_sarif_fails_closed_and_accepts_a_clean_completed_analysis(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            with self.assertRaises(ValueError):
                check(directory)
            report = directory / "csharp.sarif"
            run = {"tool": {"driver": {"name": "CodeQL"}}, "results": [], "invocations": [{"executionSuccessful": True}]}
            data = {"version": "2.1.0", "runs": [run]}
            report.write_text(json.dumps(data), encoding="utf-8")
            self.assertEqual(1, check(directory))
            invalid_reports = [
                {**data, "runs": []},
                {**data, "runs": [{**run, "results": [{}]}]},
                {**data, "runs": [{**run, "results": None}]},
                {**data, "runs": [{**run, "tool": {"driver": {"name": "Other"}}}]},
                {**data, "runs": [{**run, "invocations": [{"executionSuccessful": False}]}]},
                {**data, "runs": [{**run, "invocations": [{"toolExecutionNotifications": [{"level": "error"}]}]}]},
            ]
            for invalid in invalid_reports:
                with self.subTest(report=invalid), self.assertRaises(ValueError):
                    report.write_text(json.dumps(invalid), encoding="utf-8")
                    check(directory)
            with self.assertRaises(ValueError):
                report.write_text("invalid json", encoding="utf-8")
                check(directory)


if __name__ == "__main__":
    unittest.main()
