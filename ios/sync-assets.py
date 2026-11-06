#!/usr/bin/env python3
"""Copy only the current tablet entry-point dependencies into the iOS bundle.

This uses Python's standard library. It does not fetch network assets or include PC
configuration, signing material, runtime data, or desktop-only HTML.
"""
from html.parser import HTMLParser
from pathlib import Path, PurePosixPath
import json
import hashlib
import re
import shutil

IOS = Path(__file__).resolve().parent
SOURCE = IOS.parent / "companion" / "wwwroot"
DESTINATION = IOS / "www"


def safe_asset(value: str, relative_to: str = "") -> str:
    if not value or any(char in value for char in ("\\", "?", "#", ":")) or value.startswith("/"):
        raise ValueError(f"Unsupported non-local tablet asset: {value!r}")
    candidate = PurePosixPath(relative_to) / value
    parts = []
    for part in candidate.parts:
        if part == "..":
            if not parts:
                raise ValueError("Asset escapes the shared UI root")
            parts.pop()
        elif part != ".":
            parts.append(part)
    normalized = "/".join(parts)
    source = (SOURCE / normalized).resolve()
    if not source.is_relative_to(SOURCE.resolve()) or not source.is_file():
        raise ValueError(f"Missing shared tablet asset: {normalized}")
    if source.stat().st_size > 12 * 1024 * 1024:
        raise ValueError(f"Oversized tablet asset: {normalized}")
    return normalized


class Assets(HTMLParser):
    def __init__(self):
        super().__init__()
        self.names = {"index.html"}

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        key = "src" if tag in ("script", "img") else "href" if tag == "link" else None
        if key and attrs.get(key):
            if attrs[key].startswith("data:"):
                return
            self.names.add(safe_asset(attrs[key]))


def main():
    parser = Assets()
    parser.feed((SOURCE / "index.html").read_text(encoding="utf-8"))
    vendor = json.loads((IOS.parent / "scripts" / "notebook-pdf-vendor.json").read_text(encoding="utf-8-sig"))
    vendor_entries = vendor.get("assets")
    if not isinstance(vendor_entries, list) or not 1 <= len(vendor_entries) <= 512:
        raise ValueError("Invalid notebook PDF asset count")
    total_vendor_bytes = 0
    vendor_names = set()
    for entry in vendor_entries:
        name = safe_asset(entry.get("path", ""))
        if not name.startswith(("vendor/pdfjs/", "vendor/pdf-lib/")) or name.casefold() in vendor_names:
            raise ValueError("Unexpected or duplicate notebook PDF asset")
        data = (SOURCE / name).read_bytes()
        if len(data) != entry.get("bytes") or hashlib.sha256(data).hexdigest() != entry.get("sha256"):
            raise ValueError("Notebook PDF asset checksum or size mismatch")
        total_vendor_bytes += len(data)
        if total_vendor_bytes > 64 * 1024 * 1024:
            raise ValueError("Notebook PDF assets exceed bundle budget")
        vendor_names.add(name.casefold())
        parser.names.add(name)
    pending = list(parser.names)
    names = set(parser.names)
    while pending:
        name = pending.pop()
        if name.endswith(".css"):
            css = (SOURCE / name).read_text(encoding="utf-8")
            for match in re.finditer(r"url\(\s*['\"]?([^)'\"\s]+)['\"]?\s*\)", css):
                if match.group(1).startswith("data:"):
                    continue
                dependency = safe_asset(match.group(1), str(PurePosixPath(name).parent))
                if dependency not in names:
                    names.add(dependency)
                    pending.append(dependency)
    # The native handler serves only this manifest, so removed dependencies cannot be requested.
    DESTINATION.mkdir(parents=True, exist_ok=True)
    for name in sorted(names):
        source = SOURCE / name
        destination = DESTINATION / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
    (DESTINATION / "asset-list.json").write_text(json.dumps(sorted(names), indent=2) + "\n", encoding="utf-8")
    print(f"Synced {len(names)} shared tablet assets to {DESTINATION}")


if __name__ == "__main__":
    main()
