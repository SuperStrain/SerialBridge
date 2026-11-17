"""中文 Release 文案生成：不依赖 GitHub 网络的确定性单元测试。"""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import io
import json

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "generate_release_notes.py"
spec = importlib.util.spec_from_file_location("generate_release_notes", SCRIPT)
notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notes)


class ChineseReleaseNotesTests(unittest.TestCase):
    def test_previous_tag_uses_semantic_versions(self):
        self.assertEqual("v1.3.17", notes.previous_tag("v1.3.18", [
            "v1.3.9", "v1.3.17", "v1.4.0", "preview", "v1.3.16",
        ]))
        self.assertIsNone(notes.previous_tag("v1.0.0", ["v1.0.0"]))

    def test_chinese_curated_pr_note_and_download(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            (folder / "32.md").write_text(
                "分类：新增功能\n说明：客户端连接历史采用 SQLite 持久化存储\n"
                "注意：删除映射时会同时清理历史记录\n", encoding="utf-8")
            changes = notes.collect_changes([
                ("abcdef123456", "feat: persist per-mapping TCP client connection history in SQLite (#32)"),
                ("abc222", "chore(release): prepare v1.3.18"),
            ], folder)
            self.assertEqual(1, len(changes))
            body = notes.render("v1.3.18", "v1.3.17", changes, {}, "SuperStrain/SerialBridge")
            self.assertIn("## 新增功能", body)
            self.assertIn("客户端连接历史采用 SQLite 持久化存储", body)
            self.assertIn("## 注意事项", body)
            self.assertIn("## 下载", body)
            self.assertIn("SerialBridge-Setup-1.3.18-x64.exe", body)
            self.assertIn("SerialBridge-Portable-1.3.18-win-x64.zip", body)
            self.assertIn("compare/v1.3.17...v1.3.18", body)
            self.assertNotIn("What's Changed", body)
            self.assertNotIn("persist per-mapping", body)

    def test_untranslated_english_titles_never_leak(self):
        with tempfile.TemporaryDirectory() as tmp:
            changes = notes.collect_changes([
                ("a1", "fix: restore updater on slow systems (#38)"),
                ("a2", "feat: add command palette (#39)"),
                ("a3", "docs: update README (#40)"),
                ("a4", "perf: reduce network queue overhead"),
            ], Path(tmp))
            body = notes.render("v2.0.0", "v1.9.9", changes, {}, "SuperStrain/SerialBridge")
            self.assertIn("修复已知问题", body)
            self.assertIn("新增功能改进", body)
            self.assertIn("优化现有功能", body)
            self.assertNotIn("restore updater", body)
            self.assertNotIn("command palette", body)
            self.assertNotIn("README", body)
            self.assertEqual(3, len(changes))

    def test_no_new_changes_is_valid_manual_release(self):
        body = notes.render("v1.3.18", "v1.3.17", [], {}, "SuperStrain/SerialBridge")
        self.assertIn("本版本为维护发布", body)
        self.assertIn("## 下载", body)

    def test_first_parent_includes_one_merge_pr_not_internal_commits(self):
        # A real merge has many internal commits, but only its single
        # first-parent merge commit should be counted in the release notes.
        calls = []
        def fake_git(*args):
            calls.append(args)
            if args[0] == "log":
                self.assertIn("--first-parent", args)
                return "merge32\tMerge pull request #32 from feature/history\nmainfix\tfix: 修复安装失败"
            if args[0] == "show":
                return "Merge pull request #32 from feature/history\n\nfeat: persist history in SQLite"
            raise AssertionError(args)

        with patch.object(notes, "git", side_effect=fake_git):
            entries = notes.first_parent_commits("v1.3.16")
        self.assertEqual(2, len(entries))
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "32.md").write_text(
                "分类：新增功能\n说明：新增 SQLite 客户端历史\n", encoding="utf-8")
            changes = notes.collect_changes(entries, Path(tmp))
        self.assertEqual(2, len(changes))
        self.assertEqual("新增 SQLite 客户端历史", changes[0]["description"])
        self.assertEqual("#32", changes[0]["link"])
        self.assertEqual("修复安装失败", changes[1]["description"])

    def test_failed_tag_is_not_used_as_previous_release(self):
        # Unpublished v1.3.18 must never truncate a v1.3.19 changelog.
        self.assertEqual("v1.3.17", notes.previous_tag(
            "v1.3.19", ["v1.3.16", "v1.3.17"]))
        with patch.object(notes.urllib.request, "urlopen") as mock_api:
            mock_api.return_value.__enter__.return_value = io.BytesIO(
                json.dumps([
                    {"tag_name": "v1.3.17", "draft": False, "prerelease": False},
                    {"tag_name": "v1.3.18", "draft": True, "prerelease": False},
                    {"tag_name": "v1.3.16", "draft": False, "prerelease": False},
                    {"tag_name": "v1.3.18-rc1", "draft": False, "prerelease": True},
                ]).encode("utf-8"))
            published = notes.published_release_tags("SuperStrain/SerialBridge", "test-token")
        self.assertEqual("v1.3.17", notes.previous_tag("v1.3.19", published))

    def test_release_status_does_not_fail_on_windows_cp1252_stdout(self):
        # Package artifacts and the UTF-8 notes file must not be discarded just
        # because a CI runner cannot encode the Chinese confirmation message.
        stream = io.TextIOWrapper(io.BytesIO(), encoding="cp1252")
        with patch("sys.stdout", stream):
            notes.print_release_status(Path("dist/release-notes.md"), 3)
            stream.flush()
            message = stream.buffer.getvalue().decode("cp1252")
        self.assertIn("Release notes written:", message)
        self.assertIn(str(Path("dist/release-notes.md")), message)

    def test_release_status_keeps_chinese_with_utf8_stdout(self):
        stream = io.TextIOWrapper(io.BytesIO(), encoding="utf-8")
        with patch("sys.stdout", stream):
            notes.print_release_status(Path("dist/release-notes.md"), 3)
            stream.flush()
            message = stream.buffer.getvalue().decode("utf-8")
        self.assertIn("已生成中文发布说明", message)

    def test_missing_release_api_credentials_fails_closed(self):
        with self.assertRaises(RuntimeError):
            notes.published_release_tags("SuperStrain/SerialBridge", "")

    def test_invalid_curated_note_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = Path(tmp) / "32.md"
            p.write_text("Classification: feat\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                notes.parse_note(p)
            p.write_text("分类：任意分类\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                notes.parse_note(p)


if __name__ == "__main__":
    unittest.main()
