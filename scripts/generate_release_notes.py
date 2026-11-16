#!/usr/bin/env python3
"""生成仅含中文叙述的 GitHub Release 正式说明（不调用生成式 AI）。"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import subprocess
import json
import urllib.request

CATEGORIES = ("新增功能", "问题修复", "功能优化", "其他更新")
CATEGORY_BY_TYPE = {
    "feat": "新增功能", "feature": "新增功能",
    "fix": "问题修复", "bugfix": "问题修复",
    "perf": "功能优化", "refactor": "功能优化", "improve": "功能优化",
    "docs": "其他更新", "build": "其他更新", "ci": "其他更新",
    "test": "其他更新", "chore": "其他更新",
}
GENERIC = {
    "新增功能": "新增功能改进",
    "问题修复": "修复已知问题",
    "功能优化": "优化现有功能",
    "其他更新": "更新项目维护内容",
}
SEMVER = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)$")
PR_SUFFIX = re.compile(r"\(#(\d+)\)|Merge pull request #(\d+)", re.I)
TYPE_PREFIX = re.compile(r"^(\w+)(?:\([^)]*\))?!?:\s*")
CHINESE = re.compile(r"[\u3400-\u9fff]")
NOTES_LINE = re.compile(r"^(分类|说明|注意|概述)：\s*(.+)$")


def git(*args: str) -> str:
    return subprocess.check_output(["git", *args], text=True, encoding="utf-8").strip()


def previous_tag(tag: str, tags: list[str]) -> str | None:
    target = SEMVER.fullmatch(tag)
    if not target:
        raise ValueError("发布版本号格式不正确")
    version = tuple(map(int, target.groups()))
    older = [(tuple(map(int, m.groups())), value)
             for value in tags if (m := SEMVER.fullmatch(value))
             and tuple(map(int, m.groups())) < version]
    return max(older)[1] if older else None


def published_release_tags(repo: str, token: str) -> list[str]:
    """Query actual stable GitHub Releases, not local tags which may have failed publishing."""
    if not token:
        raise RuntimeError("缺少 GITHUB_TOKEN，无法确认上一正式发布版本")
    results: list[str] = []
    for page in range(1, 11):
        request = urllib.request.Request(
            f"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}",
            headers={
                "Authorization": f"Bearer {token}",
                "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "serialbridge-release-notes",
            },
        )
        with urllib.request.urlopen(request, timeout=30) as response:
            batch = json.load(response)
        results.extend(item["tag_name"] for item in batch
                       if not item["draft"] and not item["prerelease"]
                       and SEMVER.fullmatch(item["tag_name"]))
        if len(batch) < 100:
            return results
    raise RuntimeError("Release 列表超出安全分页上限")


def first_parent_commits(prior: str) -> list[tuple[str, str]]:
    """Represent merged PRs as one mainline commit; retain direct main commits."""
    subjects = git("log", "--first-parent", "--format=%H%x09%s", f"{prior}..HEAD")
    result = []
    for line in subjects.splitlines():
        if "\t" not in line:
            continue
        sha, subject = line.split("\t", 1)
        if re.match(r"^Merge pull request #\d+\b", subject, re.I):
            body = git("show", "-s", "--format=%B", sha)
            title = next((line.strip() for line in body.splitlines()[1:]
                          if line.strip()), "")
            if title and TYPE_PREFIX.match(title):
                pr_number = PR_SUFFIX.search(subject).group(2)
                subject = f"{title} (#{pr_number})"
        result.append((sha, subject))
    return result


def parse_note(path: Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    result: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        match = NOTES_LINE.fullmatch(line)
        if not match:
            raise ValueError(f"中文发布说明格式错误：{path}: {line}")
        key, value = match.groups()
        if key in result:
            raise ValueError(f"中文发布说明字段重复：{path}: {key}")
        result[key] = value
    if result.get("分类") and result["分类"] not in CATEGORIES:
        raise ValueError(f"不支持的变更分类：{path}: {result['分类']}")
    return result


def classify(subject: str) -> str:
    match = TYPE_PREFIX.match(subject)
    return CATEGORY_BY_TYPE.get(match.group(1).lower(), "其他更新") if match else "其他更新"


def collect_changes(commits: list[tuple[str, str]], notes_dir: Path) -> list[dict[str, str]]:
    """每个 PR 只生成一条说明；跳过自动版本提交和文档/CI 噪声。"""
    seen: set[str] = set()
    results = []
    for sha, subject in commits:
        if re.match(r"^chore\(release\):", subject, re.I):
            continue
        match = PR_SUFFIX.search(subject)
        number = next((x for x in match.groups() if x), None) if match else None
        identity = f"pr-{number}" if number else f"commit-{sha}"
        if identity in seen:
            continue
        seen.add(identity)
        note = parse_note(notes_dir / f"{number or sha}.md")
        category = note.get("分类", classify(subject))
        # Prefer Chinese descriptions supplied by maintainers. English commit/PR
        # titles are deliberately not copied into Chinese-only release bodies.
        title = TYPE_PREFIX.sub("", PR_SUFFIX.sub("", subject)).strip(" -")
        description = note.get("说明") or (title if CHINESE.search(title) else GENERIC[category])
        # Non-user-facing maintenance commits are not release highlights unless
        # the maintainer explicitly supplied a Chinese note.
        if category == "其他更新" and not note and not subject.startswith("Merge pull request #"):
            continue
        link = f"#{number}" if number else sha[:7]
        results.append({"category": category, "description": description,
                        "link": link, "warning": note.get("注意", "")})
    return results


def render(tag: str, prior: str | None, changes: list[dict[str, str]],
           highlights: dict[str, str], repo: str) -> str:
    match = SEMVER.fullmatch(tag)
    if not match:
        raise ValueError("发布版本号格式不正确")
    version = ".".join(match.groups())
    totals = {c: sum(item["category"] == c for item in changes) for c in CATEGORIES}
    summary = highlights.get("概述")
    if not summary:
        fragments = [f"{totals[c]} 项{c}" for c in CATEGORIES if totals[c]]
        summary = "本版本包含" + "、".join(fragments) + "。" if fragments else "本版本为维护发布，暂无需要单独说明的用户可见变更。"

    lines = [f"# SerialBridge v{version}", "", summary, ""]
    for category in CATEGORIES:
        members = [item for item in changes if item["category"] == category]
        if not members:
            continue
        lines += [f"## {category}", ""]
        for item in members:
            link = item["link"]
            url = (f"https://github.com/{repo}/pull/{link[1:]}"
                   if link.startswith("#") else f"https://github.com/{repo}/commit/{link}")
            lines.append(f"- **{item['description']}**（[变更记录]({url})）")
        lines.append("")
    warnings = list(dict.fromkeys([highlights.get("注意", "")] +
                                  [item["warning"] for item in changes]))
    warnings = [w for w in warnings if w]
    if warnings:
        lines += ["## 注意事项", ""] + [f"- {w}" for w in warnings] + [""]

    lines += [
        "## 下载", "",
        f"- **安装版：** `SerialBridge-Setup-{version}-x64.exe`（适合常规安装及升级）",
        f"- **绿色版：** `SerialBridge-Portable-{version}-win-x64.zip`（解压即用）",
        "", "## 完整变更记录", "",
    ]
    if prior:
        lines.append(f"[查看 {prior} 至 v{version} 的全部代码变更](https://github.com/{repo}/compare/{prior}...v{version})")
    else:
        lines.append(f"[查看项目代码](https://github.com/{repo})")
    return "\n".join(lines) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True, help="例如 v1.3.18")
    parser.add_argument("--output", required=True)
    parser.add_argument("--repo", default=os.getenv("GITHUB_REPOSITORY", "SuperStrain/SerialBridge"))
    parser.add_argument("--previous-tag", help="离线预览时指定上一已发布正式版")
    parser.add_argument("--notes-dir", default="release-notes/changes")
    parser.add_argument("--highlights-dir", default="release-notes/highlights")
    args = parser.parse_args()

    if args.previous_tag:
        prior = args.previous_tag
        if not SEMVER.fullmatch(prior) or tuple(map(int, SEMVER.fullmatch(prior).groups())) >= tuple(map(int, SEMVER.fullmatch(args.tag).groups())):
            raise ValueError("上一正式版必须是更早的有效语义化版本号")
    else:
        prior = previous_tag(args.tag, published_release_tags(
            args.repo, os.getenv("GITHUB_TOKEN", "")))
        if prior is None:
            raise RuntimeError("未找到之前已发布的正式版本，请显式提供 --previous-tag")
    # HEAD is the exact build commit. Only mainline commits are counted.
    commits = first_parent_commits(prior)
    changes = collect_changes(commits, Path(args.notes_dir))
    highlights = parse_note(Path(args.highlights_dir) / f"{args.tag}.md")
    result = render(args.tag, prior, changes, highlights, args.repo)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(result, encoding="utf-8")
    print(f"已生成中文发布说明：{output}（{len(changes)} 项变更）")


if __name__ == "__main__":
    main()
