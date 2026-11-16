"""中文 Release 文案生成：不依赖 GitHub 网络的确定性单元测试。"""
import importlib.util
from pathlib import Path
import tempfile
import unittest

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
