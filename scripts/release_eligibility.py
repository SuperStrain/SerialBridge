#!/usr/bin/env python3
"""Decide version and eligibility for scheduled or explicitly requested releases."""
import datetime as dt
import json
import os
import re
import subprocess
import argparse
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


def main(manual=False):
    write_output(eligible="false")
    releases = []
    for page in range(1, 11):
        batch = api(f"https://api.github.com/repos/{REPO}/releases?per_page=100&page={page}")
        releases.extend(r for r in batch if not r["draft"] and not r["prerelease"]
                        and re.fullmatch(r"v?\d+\.\d+\.\d+", r["tag_name"]))
        if len(batch) < 100:
            break
    else:
        raise RuntimeError("Too many releases to determine latest version safely")
    if not releases:
        raise RuntimeError("No stable semantic-version releases found")
    release = max(releases, key=lambda r: version_tuple(r["tag_name"]))
    tag = release["tag_name"]
    last = version_tuple(tag)
    published = dt.datetime.fromisoformat(release["published_at"].replace("Z", "+00:00"))
    age = NOW - published
    if not manual and age < dt.timedelta(days=4):
        print(f"Skip: {tag} released {age.days} days ago (<4 days).")
        return

    git("rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}")
    if subprocess.run(["git", "merge-base", "--is-ancestor", tag, "HEAD"], check=False).returncode:
        raise RuntimeError(f"Release tag {tag} is not an ancestor of main; refusing release.")

    changed = git("diff", "--name-only", tag, "HEAD").splitlines()
    relevant = [p for p in changed if p.startswith(RELEVANT)]
    if not manual and not relevant:
        print(f"Skip: no releasable changes since {tag}.")
        return

    current = version_tuple(ET.parse("src/SerialBridge.App/SerialBridge.App.csproj")
                            .getroot().findtext("./PropertyGroup/Version"))
    candidate = (last[0], last[1], last[2] + 1)
    next_version = max(candidate, current)
    version = ".".join(map(str, next_version))
    reason = "explicit manual release (schedule gates bypassed)" if manual else f"{len(relevant)} relevant changed files since {tag}"
    print(f"Eligible: {reason}; next version v{version}.")
    write_output(eligible="true", version=version, source_sha=git("rev-parse", "HEAD"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manual", action="store_true",
                        help="Explicit release: bypass the 4-day window and changed-file gate")
    args = parser.parse_args()
    try:
        main(manual=args.manual)
    except (ValueError, RuntimeError, urllib.error.HTTPError, subprocess.CalledProcessError) as exc:
        print(f"Release eligibility check failed: {exc}", file=sys.stderr)
        sys.exit(1)
