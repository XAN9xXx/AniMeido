import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import website_version as site


class WebsiteVersionTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.assets = Path(temp.name)
        self.version = "3.0.0+build"
        self.name = "AniMeido_setup_3.0.0+build.exe"
        self.binary = b"MZ" + bytes(100)
        (self.assets / self.name).write_bytes(self.binary)
        self.release = {"channel": "stable", "version": self.version, "notes": "升级说明"}
        self.legacy = {"latestVersion": "3.0.0", "downloadUrl": "https://dl.animeido.com/" + self.name,
                       "minAppVersion": "1.0.0", "releaseNotes": "升级说明"}
        self.write("release.json", self.release)
        self.write("version.json", self.legacy)

    def write(self, name, obj):
        path = self.assets / name
        path.write_text(json.dumps(obj), encoding="utf-8")
        return path

    def verify(self, installer, manifest=None):
        def fetch(request, **_):
            self.assertEqual(f"AniMeido-ReleaseVerifier/{self.version}", request.get_header("User-agent"))
            url = request.full_url
            if url == self.legacy["downloadUrl"]:
                if isinstance(installer, Exception):
                    raise installer
                return io.BytesIO(installer)
            self.assertEqual("https://dl.animeido.com/updates/win-x64/stable/release.json", url)
            return io.BytesIO((self.assets / "release.json").read_bytes() if manifest is None else manifest)
        with patch.object(site, "urlopen", side_effect=fetch):
            site.verify_public(self.version, self.assets)

    def test_public_installer_must_match_the_build(self):
        self.verify(self.binary)
        for body in [b"", self.binary[:-1], self.binary + b"extra", b"<html>not found</html>", OSError("404")]:
            with self.subTest(body=body), self.assertRaises((ValueError, OSError)):
                self.verify(body)

    def test_old_run_cannot_advertise_after_channel_has_advanced(self):
        with self.assertRaises(ValueError):
            self.verify(self.binary, b'{"version":"3.0.1"}')

    def test_preview_or_mismatched_url_or_notes_rejected_before_request(self):
        for file, obj in [("release.json", dict(self.release, channel="preview")),
                          ("version.json", dict(self.legacy, downloadUrl="https://example.org/test")),
                          ("version.json", dict(self.legacy, releaseNotes="unrelated"))]:
            with self.subTest(obj=obj), patch.object(site, "urlopen") as get:
                self.write(file, obj)
                with self.assertRaises(ValueError):
                    site.verify_public(self.version, self.assets)
                get.assert_not_called()
                self.write(file, self.release if file == "release.json" else self.legacy)

    def test_promote_copies_generated_file_and_identical_rerun_is_a_noop(self):
        dest = self.write("website.json", {"latestVersion": "2.1.1"})
        site.promote(self.version, self.assets, dest)
        self.assertEqual((self.assets / "version.json").read_bytes(), dest.read_bytes())
        dest.write_text(json.dumps(self.legacy, indent=4), encoding="utf-8")
        before = dest.read_bytes()
        site.promote(self.version, self.assets, dest)
        self.assertEqual(before, dest.read_bytes())

    def test_never_overwrite_newer_or_conflicting_or_invalid_entry(self):
        for version in ("3.1.0", "3.0.0", "bad"):
            dest = self.write("website.json", {"latestVersion": version})
            before = dest.read_bytes()
            with self.assertRaises(ValueError):
                site.promote(self.version, self.assets, dest)
            self.assertEqual(before, dest.read_bytes())
