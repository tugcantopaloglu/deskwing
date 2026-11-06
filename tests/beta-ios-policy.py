#!/usr/bin/env python3
"""Regression tests for beta signing identity and artifact-boundary checks."""
import datetime as dt
import importlib.util
from pathlib import Path
import plistlib
import json
import os
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock
import zipfile
import zlib

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("beta_ios", Path(__file__).resolve().parents[1] / "scripts" / "beta-ios.py")
beta = importlib.util.module_from_spec(spec)
spec.loader.exec_module(beta)


class BetaPolicyTests(unittest.TestCase):
    def profile(self):
        return {"UUID": "00000000-0000-4000-8000-000000000000", "TeamIdentifier": ["ABCD123456"],
                "ApplicationIdentifierPrefix": ["LEGACY1234"], "ExpirationDate": dt.datetime(2099, 1, 1),
                "Entitlements": {"application-identifier": "LEGACY1234.app.deskwing.tablet",
                                 "com.apple.developer.team-identifier": "ABCD123456", "get-task-allow": False},
                "DeveloperCertificates": [b"public-certificate-fixture"]}

    def test_exact_existing_identity_and_build_number(self):
        self.assertEqual(beta.identity_inputs("ABCD123456", beta.BUNDLE_ID, "17")[2], "17")
        for team, bundle, build in [("wrong", beta.BUNDLE_ID, "1"), ("ABCD123456", "other.app", "1"),
                                    ("ABCD123456", beta.BUNDLE_ID, "0"), ("ABCD123456", beta.BUNDLE_ID, "1\nX=1")]:
            with self.subTest(team=team, bundle=bundle, build=build), self.assertRaises(ValueError):
                beta.identity_inputs(team, bundle, build)

    def test_branch_push_cannot_select_signing(self):
        for supplied_mode in ("", "unsigned-validation", "signed-archive"):
            self.assertEqual(beta.request_mode("push", "refs/heads/codex/workspace-expansion", supplied_mode), "unsigned-validation")
        self.assertEqual(beta.request_mode("workflow_dispatch", "refs/heads/main", "signed-archive"), "signed-archive")
        self.assertEqual(beta.request_mode("workflow_dispatch", "refs/heads/feature", "unsigned-validation"), "unsigned-validation")
        for event, reference, mode in [("push", "refs/heads/main", "signed-archive"),
                                       ("workflow_dispatch", "refs/heads/feature", "signed-archive"),
                                       ("pull_request", "refs/pull/1/merge", "signed-archive"),
                                       ("workflow_dispatch", "refs/heads/main", "other")]:
            with self.subTest(event=event, reference=reference, mode=mode), self.assertRaises(ValueError):
                beta.request_mode(event, reference, mode)

    def test_app_store_profile_and_transferred_prefix(self):
        self.assertEqual(beta.validate_profile(self.profile(), "ABCD123456", beta.BUNDLE_ID), self.profile()["UUID"])

    def test_signing_membership_uses_identity_field_not_certificate_title(self):
        profile = self.profile()
        fingerprint = beta.hashlib.sha1(profile["DeveloperCertificates"][0], usedforsecurity=False).hexdigest().upper()
        with mock.patch.object(beta.subprocess, "run", return_value=SimpleNamespace(stdout=f'  1) {fingerprint} "Apple Distribution: DeskWing"\n')):
            self.assertEqual(beta.signing_identity(Path("fixed.keychain-db"), profile), fingerprint)
        with mock.patch.object(beta.subprocess, "run", return_value=SimpleNamespace(stdout=f'  1) {"F" * 40} "Title containing {fingerprint}"\n')):
            with self.assertRaises(ValueError): beta.signing_identity(Path("fixed.keychain-db"), profile)

    def test_reject_wrong_team_or_wildcard(self):
        profile = self.profile(); profile["TeamIdentifier"] = ["OTHER12345"]
        with self.assertRaises(ValueError): beta.validate_profile(profile, "ABCD123456", beta.BUNDLE_ID)
        profile = self.profile(); profile["Entitlements"]["application-identifier"] = "LEGACY1234.*"
        with self.assertRaises(ValueError): beta.validate_profile(profile, "ABCD123456", beta.BUNDLE_ID)

    def test_reject_expired_debug_device_or_enterprise_profile(self):
        for change in ("expired", "debug", "devices", "enterprise"):
            profile = self.profile()
            if change == "expired": profile["ExpirationDate"] = dt.datetime(2000, 1, 1)
            if change == "debug": profile["Entitlements"]["get-task-allow"] = True
            if change == "devices": profile["ProvisionedDevices"] = ["device-id"]
            if change == "enterprise": profile["ProvisionsAllDevices"] = True
            with self.subTest(change=change), self.assertRaises(ValueError):
                beta.validate_profile(profile, "ABCD123456", beta.BUNDLE_ID)

    def test_stage_only_ipa_and_checksums(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); export = root / "DeskWingExport"; export.mkdir()
            info = {"CFBundleIdentifier": beta.BUNDLE_ID, "CFBundleVersion": "17", "CFBundleShortVersionString": "1.15.0"}
            with zipfile.ZipFile(export / "DeskWing.ipa", "w") as archive:
                archive.writestr("Payload/DeskWing.app/Info.plist", plistlib.dumps(info))
            (export / "distribution.mobileprovision").write_bytes(b"must-not-be-staged")
            with mock.patch.dict(os.environ, {"RUNNER_TEMP": directory}):
                beta.stage_ipa(export, root / "DeskWingBeta", "ABCD123456", beta.BUNDLE_ID, "17")
            self.assertEqual({path.name for path in (root / "DeskWingBeta").iterdir()}, {"DeskWing.ipa", "beta-manifest.json", "SHA256SUMS.txt"})
            self.assertEqual((root / "DeskWingBeta" / "DeskWing.ipa").read_bytes(), (export / "DeskWing.ipa").read_bytes())

    def test_reject_wrong_exported_bundle_or_multiple_apps(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); export = root / "DeskWingExport"; export.mkdir()
            info = {"CFBundleIdentifier": "wrong.app", "CFBundleVersion": "17", "CFBundleShortVersionString": "1.15.0"}
            with zipfile.ZipFile(export / "DeskWing.ipa", "w") as archive:
                archive.writestr("Payload/DeskWing.app/Info.plist", plistlib.dumps(info))
            with mock.patch.dict(os.environ, {"RUNNER_TEMP": directory}), self.assertRaises(ValueError):
                beta.stage_ipa(export, root / "DeskWingBeta", "ABCD123456", beta.BUNDLE_ID, "17")

    def test_canonical_runner_paths_reject_escape_and_existing_artifacts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with mock.patch.dict(os.environ, {"RUNNER_TEMP": directory}):
                self.assertEqual(beta.runner_path(root / "DeskWingExport", "DeskWingExport"), root.resolve() / "DeskWingExport")
                for candidate in (root.parent / "DeskWingExport", root / "other", root / "child" / ".." / "DeskWingExport"):
                    with self.subTest(candidate=candidate), self.assertRaises(ValueError):
                        beta.runner_path(candidate, "DeskWingExport")
                export = root / "DeskWingExport"; export.mkdir()
                (root / "DeskWingBeta").mkdir()
                with self.assertRaises(ValueError):
                    beta.stage_ipa(export, root / "DeskWingBeta", "ABCD123456", beta.BUNDLE_ID, "17")
            with mock.patch.dict(os.environ, {"RUNNER_TEMP": ""}), self.assertRaises(ValueError):
                beta.runner_path(root / "DeskWingExport", "DeskWingExport")

    def test_source_hardlink_and_oversize_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); source = root / "source.ipa"; source.write_bytes(b"test")
            with self.assertRaises(ValueError): beta.open_owned_file(source, 3)
            os.link(source, root / "linked.ipa")
            with self.assertRaises(ValueError): beta.open_owned_file(source, 100)
            self.assertEqual(source.read_bytes(), b"test")

    def test_symbolic_export_directory_and_file_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); outside = root / "outside"; outside.mkdir()
            export = root / "DeskWingExport"
            try:
                export.symlink_to(outside, target_is_directory=True)
            except OSError:
                self.skipTest("This local account cannot create symlinks; the macOS runner executes this check.")
            with mock.patch.dict(os.environ, {"RUNNER_TEMP": directory}), self.assertRaises(ValueError):
                beta.runner_path(export, "DeskWingExport")
            actual = outside / "real.ipa"; actual.write_bytes(b"test")
            linked = outside / "linked.ipa"; linked.symlink_to(actual)
            with self.assertRaises(ValueError): beta.open_owned_file(linked, 100)

    def test_github_environment_file_is_never_opened(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); signing = root / "deskwing-ios-signing"; signing.mkdir()
            profile = signing / "profile.plist"; profile.write_bytes(plistlib.dumps(self.profile()))
            environment = root / "outside-env"; environment.write_text("unchanged")
            with mock.patch.dict(os.environ, {"RUNNER_TEMP": directory, "GITHUB_ENV": str(environment)}), \
                 mock.patch.object(beta, "signing_identity", return_value="A" * 40):
                beta.profile_options(profile, signing / "ExportOptions.plist", signing / "signing.keychain-db", "ABCD123456", beta.BUNDLE_ID, "17")
            self.assertEqual(environment.read_text(), "unchanged")

    def test_store_icon_rejects_alpha_wrong_size_and_path(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog = Path(directory)
            def contents(filename):
                (catalog / "Contents.json").write_text(json.dumps({"images": [{"idiom": "universal", "size": "1024x1024", "filename": filename}]}))
            contents("AppIcon.png")
            def chunk(kind, payload):
                return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xffffffff)
            def image(width, color, transparency=False):
                header = chunk(b"IHDR", struct.pack(">II", width, 1024) + bytes([8, color, 0, 0, 0]))
                optional = chunk(b"tRNS", b"\0" * 6) if transparency else b""
                (catalog / "AppIcon.png").write_bytes(b"\x89PNG\r\n\x1a\n" + header + optional + chunk(b"IDAT", zlib.compress(b"\0" * 8)) + chunk(b"IEND", b""))
            image(1024, 2); beta.validate_icon(catalog)
            image(1024, 2, transparency=True)
            with self.assertRaises(ValueError): beta.validate_icon(catalog)
            image(1024, 6)
            with self.assertRaises(ValueError): beta.validate_icon(catalog)
            image(512, 2)
            with self.assertRaises(ValueError): beta.validate_icon(catalog)
            contents("../AppIcon.png")
            with self.assertRaises(ValueError): beta.validate_icon(catalog)


if __name__ == "__main__":
    unittest.main()
