"""Regression tests for forced/manual and scheduled release eligibility."""
import datetime as dt
import importlib.util
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

# The module reads GitHub environment variables at import time.
os.environ.setdefault("GITHUB_REPOSITORY", "SuperStrain/SerialBridge")
os.environ.setdefault("GITHUB_TOKEN", "test-token")
SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "release_eligibility.py"
spec = importlib.util.spec_from_file_location("release_eligibility", SCRIPT)
eligibility = importlib.util.module_from_spec(spec)
spec.loader.exec_module(eligibility)


class ReleaseEligibilityTests(unittest.TestCase):
    def run_case(self, *, manual, age_days, changed_files):
        outputs = {}
        release = {
            "draft": False,
            "prerelease": False,
            "tag_name": "v1.3.13",
            "published_at": (eligibility.NOW - dt.timedelta(days=age_days))
                            .isoformat().replace("+00:00", "Z"),
        }

        def fake_git(*args):
            if args[0] == "diff":
                return "\n".join(changed_files)
            if args[0] == "rev-parse" and args[-1] == "HEAD":
                return "feed123"
            return "oldtag"

        class FakeTree:
            @staticmethod
            def getroot():
                class Root:
                    @staticmethod
                    def findtext(_):
                        return "1.3.13"
                return Root()
        with patch.object(eligibility, "api", return_value=[release]), \
                patch.object(eligibility, "git", side_effect=fake_git), \
                patch.object(eligibility.subprocess, "run",
                             return_value=subprocess.CompletedProcess([], 0)), \
                patch.object(eligibility.ET, "parse", return_value=FakeTree()), \
                patch.object(eligibility, "write_output", side_effect=lambda **v: outputs.update(v)):
            eligibility.main(manual=manual)
        return outputs

    def test_manual_release_ignores_four_day_window_and_absence_of_changes(self):
        result = self.run_case(manual=True, age_days=0, changed_files=[])
        self.assertEqual(result["eligible"], "true")
        self.assertEqual(result["version"], "1.3.14")
        self.assertEqual(result["source_sha"], "feed123")

    def test_scheduled_release_keeps_four_day_window(self):
        result = self.run_case(manual=False, age_days=1, changed_files=["src/change.cs"])
        self.assertEqual(result, {"eligible": "false"})

    def test_scheduled_release_still_requires_source_changes(self):
        result = self.run_case(manual=False, age_days=5, changed_files=[])
        self.assertEqual(result, {"eligible": "false"})

    def test_scheduled_release_works_when_both_conditions_met(self):
        result = self.run_case(manual=False, age_days=5, changed_files=["src/new-feature.cs"])
        self.assertEqual(result["eligible"], "true")
        self.assertEqual(result["version"], "1.3.14")


if __name__ == "__main__":
    unittest.main()
