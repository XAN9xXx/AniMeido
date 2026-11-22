"""Stable release handoff to the legacy website update feed."""
import json
import re
import sys
from pathlib import Path
from urllib.request import Request, urlopen

CDN = "https://dl.animeido.com"
NUMBER = r"(?:0|[1-9][0-9]*)"
VERSION = rf"{NUMBER}\.{NUMBER}\.{NUMBER}"


def read(path):
    return json.loads(path.read_bytes())


def website_manifest(version, assets):
    if not re.fullmatch(VERSION + r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?", version):
        raise ValueError("The website accepts stable versions only")
    manifest = read(assets / "release.json")
    legacy = read(assets / "version.json")
    expected = {"latestVersion": version.split("+")[0], "downloadUrl": f"{CDN}/AniMeido_setup_{version}.exe",
                "minAppVersion": "1.0.0", "releaseNotes": manifest["notes"]}
    if (manifest["version"] != version or manifest["channel"] != "stable"
            or not isinstance(manifest["notes"], str) or not manifest["notes"].strip() or legacy != expected):
        raise ValueError("Release and website manifest disagree")
    return legacy


def verify_public(version, assets):
    legacy = website_manifest(version, assets)
    installer = assets / f"AniMeido_setup_{version}.exe"
    if not installer.stat().st_size:
        raise ValueError("Empty installer")
    headers = {"User-Agent": f"AniMeido-ReleaseVerifier/{version}"}
    # Use the public URL without R2 credentials. A 200 HTML error or a partial installer must not pass.
    with installer.open("rb") as local, urlopen(Request(legacy["downloadUrl"], headers=headers), timeout=45) as public:
        while chunk := local.read(1024 * 1024):
            if public.read(len(chunk)) != chunk:
                raise ValueError("Public installer differs from the release artifact")
        if public.read(1):
            raise ValueError("Unexpected bytes after end of installer")
    # A failed website-only job can be rerun after another stable release has gone live.
    manifest = (assets / "release.json").read_bytes()
    with urlopen(Request(f"{CDN}/updates/win-x64/stable/release.json", headers=headers), timeout=30) as public:
        if public.read(len(manifest) + 1) != manifest:
            raise ValueError("The public stable channel does not advertise this release")


def promote(version, assets, destination):
    legacy = website_manifest(version, assets)
    if destination.is_symlink():
        raise ValueError("Expected a regular website version.json")
    current = read(destination)
    if current == legacy:
        return
    previous = current["latestVersion"]
    if not re.fullmatch(VERSION, previous):
        raise ValueError("Invalid version in the website feed")
    if tuple(map(int, previous.split("."))) >= tuple(map(int, legacy["latestVersion"].split("."))):
        raise ValueError("Refusing to downgrade or replace an existing website release")
    destination.write_bytes((assets / "version.json").read_bytes())


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "verify-public":
        verify_public(sys.argv[2], Path(sys.argv[3]))
    elif len(sys.argv) == 5 and sys.argv[1] == "promote":
        promote(sys.argv[2], Path(sys.argv[3]), Path(sys.argv[4]))
    else:
        sys.exit("usage: website_version.py verify-public VERSION ASSETS | promote VERSION ASSETS DESTINATION")
