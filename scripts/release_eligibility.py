#!/usr/bin/env python3
"""Decide whether the scheduled SerialBridge release is eligible."""
import datetime as dt
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

REPO = os.environ["GITHUB_REPOSITORY"]
TOKEN = os.environ["GITHUB_TOKEN"]
NOW = dt.datetime.now(dt.timezone.utc)
RELEVANT = ("src/", "installer/", "scripts/", ".github/workflows/build.yml", "SerialBridge.sln")


def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()


def write_output(**values):
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for key, value in values.items():
            output.write(f"{key}={value}\n")


def api(url):
    request = urllib.request.Request(
        url,
        headers={"Authorization": f"Bearer {TOKEN}", "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "serialbridge-release"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def version_tuple(value):
    match = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", value)
    if not match:
        raise ValueError(f"Unsupported release version: {value}")
    return tuple(map(int, match.groups()))


def main():
    write_output(eligible="false")
    release = api(f"https://api.github.com/repos/{REPO}/releases/latest")
    tag = release["tag_name"]
    last = version_tuple(tag)
    published = dt.datetime.fromisoformat(release["published_at"].replace("Z", "+00:00"))
    age = NOW - published
    if age < dt.timedelta(days=15):
        print(f"Skip: {tag} released {age.days} days ago (<15 days).")
        return

    git("rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}")
    if subprocess.run(["git", "merge-base", "--is-ancestor", tag, "HEAD"], check=False).returncode:
        raise RuntimeError(f"Release tag {tag} is not an ancestor of main; refusing release.")

    changed = git("diff", "--name-only", tag, "HEAD").splitlines()
    relevant = [p for p in changed if p.startswith(RELEVANT)]
    if not relevant:
        print(f"Skip: no releasable changes since {tag}.")
        return

    current = version_tuple(ET.parse("src/SerialBridge.App/SerialBridge.App.csproj")
                            .getroot().findtext("./PropertyGroup/Version"))
    candidate = (last[0], last[1], last[2] + 1)
    next_version = max(candidate, current)
    version = ".".join(map(str, next_version))
    print(f"Eligible: {len(relevant)} relevant changed files since {tag}; next version v{version}.")
    write_output(eligible="true", version=version)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, urllib.error.HTTPError, subprocess.CalledProcessError) as exc:
        print(f"Release eligibility check failed: {exc}", file=sys.stderr)
        sys.exit(1)
