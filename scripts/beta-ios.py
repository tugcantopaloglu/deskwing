#!/usr/bin/env python3
"""Validate local iOS beta inputs and stage only the exported deliverable.

This helper never uploads an app or opens an account session. Signing material is
provided by the manual GitHub Actions workflow and remains outside the checkout.
"""
from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import stat
import struct
import subprocess
import sys
import uuid
import zipfile
import zlib


BUNDLE_ID = "app.deskwing.tablet"
MAX_SECRET_BYTES = 2 * 1024 * 1024
MAX_IPA_BYTES = 512 * 1024 * 1024


def fail(message: str) -> None:
    raise ValueError(message)


def identity_inputs(team: str, bundle: str, build: str) -> tuple[str, str, str]:
    if not re.fullmatch(r"[A-Z0-9]{10}", team):
        fail("IOS_TEAM_ID must be the ten-character Apple team identifier.")
    if bundle != BUNDLE_ID:
        fail("IOS_BUNDLE_ID must match the existing app.deskwing.tablet identity.")
    if not re.fullmatch(r"[1-9][0-9]{0,8}", build):
        fail("Choose a positive beta build number with at most nine digits.")
    return team, bundle, build


def request_mode(event: str, reference: str, mode: str) -> str:
    if event == "push" and reference == "refs/heads/codex/workspace-expansion":
        return "unsigned-validation"
    if event != "workflow_dispatch" or mode not in ("unsigned-validation", "signed-archive"):
        fail("Beta preparation runs only by manual dispatch or the named unsigned integration branch.")
    if mode == "signed-archive" and reference != "refs/heads/main":
        fail("Signed beta archives may run only from reviewed main.")
    return mode


def runner_path(path: Path, relative: str) -> Path:
    runner_temp = os.environ.get("RUNNER_TEMP", "")
    root = Path(runner_temp)
    if not runner_temp or not root.is_absolute() or not root.is_dir() or root.is_symlink():
        fail("RUNNER_TEMP must identify an existing ordinary runner directory.")
    root = root.resolve(strict=True)
    expected = root / relative
    if Path(relative).is_absolute() or ".." in Path(relative).parts or ".." in path.parts or path.resolve() != expected.resolve():
        fail("Use only the workflow's fixed temporary signing and artifact paths.")
    current = root
    for part in expected.relative_to(root).parts:
        current = current / part
        if current.is_symlink():
            fail("Runner beta paths cannot contain symbolic links.")
        if current.exists():
            metadata = current.lstat()
            if getattr(metadata, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
                fail("Runner beta paths cannot contain reparse points.")
            if hasattr(os, "getuid") and metadata.st_uid != os.getuid():
                fail("Runner beta paths must belong to the current user.")
    # Return the fixed-relative path constructed from the runner root. Raw CLI
    # spellings never reach a file read/write after this boundary.
    return expected


def open_owned_file(path: Path, maximum_bytes: int):
    if path.is_symlink():
        fail("Beta source files cannot be symbolic links.")
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
    try:
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1:
            fail("Beta source must be one ordinary, unlinked file.")
        if hasattr(os, "getuid") and metadata.st_uid != os.getuid():
            fail("Beta source files must belong to the current user.")
        if metadata.st_size < 1 or metadata.st_size > maximum_bytes:
            fail("Beta source file size is outside the permitted bound.")
        return os.fdopen(descriptor, "rb"), metadata
    except BaseException:
        os.close(descriptor)
        raise


def validate_icon(catalog: Path) -> None:
    contents = json.loads((catalog / "Contents.json").read_text(encoding="utf-8"))
    images = contents.get("images", [])
    candidates = [image for image in images if image.get("idiom") == "universal" and image.get("size") == "1024x1024"]
    if len(candidates) != 1:
        fail("Configure one universal 1024px iOS App Store icon.")
    filename = candidates[0].get("filename", "")
    if not re.fullmatch(r"[A-Za-z0-9._-]+\.png", filename):
        fail("The App Store icon must be a local PNG filename.")
    image = catalog / filename
    if image.is_symlink() or image.stat().st_size > 10 * 1024 * 1024:
        fail("Use a bounded local PNG for the App Store icon.")
    data = image.read_bytes()
    header = data[:33]
    if len(header) != 33 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        fail("The App Store icon is not a valid PNG header.")
    width, height = struct.unpack(">II", header[16:24])
    if (width, height) != (1024, 1024) or header[24] != 8 or header[25] != 2:
        fail("Use a 1024px, 8-bit RGB App Store icon without an alpha channel.")
    offset, image_data, ended = 8, False, False
    while offset + 12 <= len(data):
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        end = offset + length + 12
        if end > len(data):
            fail("The App Store PNG contains a truncated chunk.")
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        checksum = struct.unpack(">I", data[end - 4:end])[0]
        if zlib.crc32(kind + payload) & 0xffffffff != checksum:
            fail("The App Store PNG contains an invalid chunk checksum.")
        if kind in (b"tRNS", b"acTL"):
            fail("The App Store icon must be opaque and static.")
        image_data |= kind == b"IDAT" and length > 0
        if kind == b"IEND":
            ended = length == 0 and end == len(data)
            break
        offset = end
    if not ended or not image_data:
        fail("The App Store PNG has no complete image stream.")


def decode_secret(name: str, destination: Path) -> None:
    value = os.environ.get(name, "")
    if not value or len(value) > MAX_SECRET_BYTES * 2:
        fail(f"Configure the {name} environment secret before signing.")
    try:
        decoded = base64.b64decode("".join(value.split()), validate=True)
    except (ValueError, base64.binascii.Error):
        fail(f"{name} must contain a valid Base64 file.")
    if not decoded or len(decoded) > MAX_SECRET_BYTES:
        fail(f"{name} exceeds the allowed signing-file size.")
    destination.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "wb") as output:
        output.write(decoded)


def validate_profile(profile: dict, team: str, bundle: str, now: dt.datetime | None = None) -> str:
    identity_inputs(team, bundle, "1")
    identifier = profile.get("UUID", "")
    try:
        if str(uuid.UUID(identifier)).upper() != identifier.upper():
            fail("The provisioning profile UUID is invalid.")
    except (ValueError, AttributeError, TypeError):
        fail("The provisioning profile UUID is invalid.")
    if team not in profile.get("TeamIdentifier", []):
        fail("The provisioning profile belongs to a different Apple team.")
    expiry = profile.get("ExpirationDate")
    if not isinstance(expiry, dt.datetime):
        fail("The provisioning profile has no valid expiration date.")
    now = now or dt.datetime.now(dt.timezone.utc)
    if expiry.tzinfo is None:
        expiry = expiry.replace(tzinfo=dt.timezone.utc)
    if expiry <= now:
        fail("The provisioning profile has expired.")
    entitlements = profile.get("Entitlements", {})
    app_identifier = entitlements.get("application-identifier", "")
    prefixes = profile.get("ApplicationIdentifierPrefix", [])
    if not any(app_identifier == prefix + "." + bundle for prefix in prefixes if isinstance(prefix, str)):
        fail("Use an explicit App Store profile for the DeskWing bundle identifier.")
    if entitlements.get("com.apple.developer.team-identifier") != team:
        fail("The profile entitlements do not match the selected Apple team.")
    if entitlements.get("get-task-allow") is not False:
        fail("Use an App Store distribution profile with debugging disabled.")
    if profile.get("ProvisionedDevices") or profile.get("ProvisionsAllDevices"):
        fail("Use an App Store distribution profile, not development, Ad Hoc, or enterprise provisioning.")
    certificates = profile.get("DeveloperCertificates", [])
    if not certificates or any(not isinstance(cert, bytes) or not cert for cert in certificates):
        fail("The profile contains no distribution signing certificate.")
    return identifier


def signing_identity(keychain: Path, profile: dict) -> str:
    result = subprocess.run(
        ["security", "find-identity", "-v", "-p", "codesigning", str(keychain)],
        check=True, capture_output=True, text=True,
    )
    available = set(re.findall(r'^\s*\d+\)\s+([0-9A-Fa-f]{40})\s+"', result.stdout, re.MULTILINE))
    # Apple's security tool identifies certificates by SHA-1 thumbprint. This is
    # an identifier comparison, not a content-integrity or signature algorithm.
    allowed = {hashlib.sha1(cert, usedforsecurity=False).hexdigest().upper() for cert in profile["DeveloperCertificates"]}
    matches = sorted({identity.upper() for identity in available} & allowed)
    if len(matches) != 1:
        fail("Import exactly one valid signing identity included in the selected provisioning profile.")
    return matches[0]


def profile_options(profile_path: Path, options_path: Path, keychain: Path, team: str, bundle: str, build: str) -> str:
    identity_inputs(team, bundle, build)
    profile_path = runner_path(profile_path, "deskwing-ios-signing/profile.plist")
    options_path = runner_path(options_path, "deskwing-ios-signing/ExportOptions.plist")
    keychain = runner_path(keychain, "deskwing-ios-signing/signing.keychain-db")
    source, _ = open_owned_file(profile_path, MAX_SECRET_BYTES)
    with source:
        profile = plistlib.load(source)
    identifier = validate_profile(profile, team, bundle)
    signer = signing_identity(keychain, profile)
    options = {
        "method": "app-store-connect", "destination": "export",
        "teamID": team, "signingStyle": "manual", "signingCertificate": signer,
        "provisioningProfiles": {bundle: identifier}, "manageAppVersionAndBuildNumber": False,
        "stripSwiftSymbols": True, "uploadSymbols": False,
    }
    with options_path.open("xb") as output:
        plistlib.dump(options, output, sort_keys=True)
    # Later steps read these validated identifiers from this private, fixed
    # ExportOptions file. The helper never opens GitHub file-command paths.
    return identifier


def stage_ipa(export_directory: Path, artifact_directory: Path, team: str, bundle: str, build: str) -> None:
    identity_inputs(team, bundle, build)
    export_directory = runner_path(export_directory, "DeskWingExport")
    artifact_directory = runner_path(artifact_directory, "DeskWingBeta")
    ipa = export_directory / "DeskWing.ipa"
    if any(entry.suffix.lower() == ".ipa" and entry.name != "DeskWing.ipa" for entry in export_directory.iterdir()):
        fail("Expected only the fixed DeskWing.ipa export.")
    if artifact_directory.exists():
        fail("Use a fresh beta artifact directory.")
    source, original = open_owned_file(ipa, MAX_IPA_BYTES)
    with source:
        with zipfile.ZipFile(source) as archive:
            entries = [entry for entry in archive.namelist() if re.fullmatch(r"Payload/[^/]+\.app/Info\.plist", entry)]
            if len(entries) != 1 or archive.getinfo(entries[0]).file_size > 1024 * 1024:
                fail("The IPA does not contain one bounded application Info.plist.")
            info = plistlib.loads(archive.read(entries[0]))
        if info.get("CFBundleIdentifier") != bundle or str(info.get("CFBundleVersion")) != build:
            fail("The exported IPA identity or build number is incorrect.")
        if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", str(info.get("CFBundleShortVersionString", ""))):
            fail("The IPA marketing version is invalid.")
        artifact_directory.mkdir(mode=0o700)
        destination = artifact_directory / "DeskWing.ipa"
        descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        hasher, copied = hashlib.sha256(), 0
        source.seek(0)
        with os.fdopen(descriptor, "wb") as output:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                copied += len(block)
                if copied > MAX_IPA_BYTES:
                    fail("The exported IPA grew beyond its size limit.")
                output.write(block); hasher.update(block)
        final = os.fstat(source.fileno())
        if (copied, final.st_size, final.st_mtime_ns, final.st_ino) != (original.st_size, original.st_size, original.st_mtime_ns, original.st_ino):
            fail("The exported IPA changed while it was staged.")
    digest = hasher.hexdigest()
    manifest = {
        "artifact": destination.name, "sha256": digest, "bytes": copied,
        "bundleIdentifier": bundle, "version": info["CFBundleShortVersionString"],
        "buildNumber": build, "teamIdentifier": team, "signed": True,
        "sourceRevision": os.environ.get("GITHUB_SHA", ""), "uploaded": False,
    }
    (artifact_directory / "beta-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (artifact_directory / "SHA256SUMS.txt").write_text(f"{digest}  {destination.name}\n", encoding="ascii")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="command", required=True)
    subcommands.add_parser("icon")
    request = subcommands.add_parser("request")
    request.add_argument("--event", required=True); request.add_argument("--ref", required=True); request.add_argument("--mode", default="")
    inputs = subcommands.add_parser("inputs")
    inputs.add_argument("--team", required=True); inputs.add_argument("--bundle", required=True); inputs.add_argument("--build", required=True)
    decode = subcommands.add_parser("decode")
    decode.add_argument("--directory", type=Path, required=True)
    profile = subcommands.add_parser("profile")
    profile.add_argument("--profile", type=Path, required=True); profile.add_argument("--options", type=Path, required=True); profile.add_argument("--keychain", type=Path, required=True)
    stage = subcommands.add_parser("stage")
    stage.add_argument("--export", type=Path, required=True); stage.add_argument("--artifacts", type=Path, required=True)
    for command in (profile, stage):
        command.add_argument("--team", required=True); command.add_argument("--bundle", required=True); command.add_argument("--build", required=True)
    args = parser.parse_args()
    try:
        if args.command == "request":
            request_mode(args.event, args.ref, args.mode)
        elif args.command == "icon":
            validate_icon(Path(__file__).resolve().parents[1] / "ios" / "Resources" / "Assets.xcassets" / "AppIcon.appiconset")
        elif args.command == "inputs":
            identity_inputs(args.team, args.bundle, args.build)
        elif args.command == "decode":
            directory = runner_path(args.directory, "deskwing-ios-signing")
            decode_secret("IOS_DISTRIBUTION_P12_BASE64", directory / "distribution.p12")
            decode_secret("IOS_APP_STORE_PROFILE_BASE64", directory / "distribution.mobileprovision")
        elif args.command == "profile":
            profile = runner_path(args.profile, "deskwing-ios-signing/profile.plist")
            options = runner_path(args.options, "deskwing-ios-signing/ExportOptions.plist")
            keychain = runner_path(args.keychain, "deskwing-ios-signing/signing.keychain-db")
            profile_options(profile, options, keychain, args.team, args.bundle, args.build)
        else:
            export = runner_path(args.export, "DeskWingExport")
            artifacts = runner_path(args.artifacts, "DeskWingBeta")
            stage_ipa(export, artifacts, args.team, args.bundle, args.build)
    except (ValueError, OSError, plistlib.InvalidFileException, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        # Do not include command output or secret values in CI diagnostics.
        print(f"Beta preparation failed: {error if isinstance(error, ValueError) else type(error).__name__}.", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
