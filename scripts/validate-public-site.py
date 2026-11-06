#!/usr/bin/env python3
"""Validate the exact static DeskWing Pages payload without fetching external URLs."""

from __future__ import annotations

import hashlib
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


REPOSITORY = Path(__file__).resolve().parent.parent
SITE = REPOSITORY / "release" / "site"
EXPECTED = {
    ".nojekyll", "index.html", "privacy.html", "support.html", "site.css",
    "assets/app-icon.svg", "assets/Manrope.ttf", "assets/OFL.txt",
}
ORIGINAL_ASSETS = {
    "assets/app-icon.svg": "assets/app-icon.svg",
    "assets/Manrope.ttf": "companion/wwwroot/fonts/Manrope.ttf",
    "assets/OFL.txt": "companion/wwwroot/fonts/OFL.txt",
}
DEVELOPER = "Tuğcan Topaloğlu"
CONTACT = "tugcan@tugcan.dev"


class Document(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.counts: dict[str, int] = {}
        self.ids: set[str] = set()
        self.references: list[str] = []
        self.metadata: dict[str, str] = {}
        self.errors: list[str] = []
        self.current_links = 0
        self.language = ""
        self.title_text: list[str] = []
        self.in_title = False

    def handle_starttag(self, tag: str, pairs: list[tuple[str, str | None]]) -> None:
        self.counts[tag] = self.counts.get(tag, 0) + 1
        attrs = dict(pairs)
        if tag in {"script", "iframe", "object", "embed", "form", "base"}:
            self.errors.append(f"Unexpected active/embedded element: {tag}")
        if any(name.lower().startswith("on") or name.lower() == "style" for name, _ in pairs):
            self.errors.append("Inline event handlers and styles are not allowed")
        if tag == "html":
            self.language = attrs.get("lang") or ""
        if tag == "title":
            self.in_title = True
        identity = attrs.get("id")
        if identity:
            if identity in self.ids:
                self.errors.append(f"Duplicate ID: {identity}")
            self.ids.add(identity)
        if attrs.get("aria-current") == "page":
            self.current_links += 1
        if tag == "img" and "alt" not in attrs:
            self.errors.append("Image is missing alt text")
        if tag == "meta":
            key = attrs.get("name") or attrs.get("http-equiv")
            if key:
                self.metadata[key.lower()] = attrs.get("content") or ""
        for attribute in ("href", "src"):
            if attrs.get(attribute):
                self.references.append(attrs[attribute] or "")

    def handle_startendtag(self, tag: str, pairs: list[tuple[str, str | None]]) -> None:
        self.handle_starttag(tag, pairs)

    def handle_endtag(self, tag: str) -> None:
        if tag == "title":
            self.in_title = False

    def handle_data(self, data: str) -> None:
        if self.in_title:
            self.title_text.append(data)


def validate() -> list[str]:
    errors: list[str] = []
    if not SITE.is_dir() or SITE.is_symlink():
        return ["The expected release/site directory is missing or is a symlink"]
    actual: set[str] = set()
    for item in SITE.rglob("*"):
        if item.is_symlink():
            errors.append(f"Symlink is not allowed: {item.relative_to(SITE).as_posix()}")
        elif item.is_file():
            actual.add(item.relative_to(SITE).as_posix())
            if item.stat().st_size > 2 * 1024 * 1024:
                errors.append(f"Unexpectedly large public asset: {item.name}")
    if actual != EXPECTED:
        errors.append(f"Public file whitelist differs; missing={sorted(EXPECTED-actual)}, extra={sorted(actual-EXPECTED)}")

    documents: dict[str, Document] = {}
    for filename in ("index.html", "privacy.html", "support.html"):
        source = SITE / filename
        if not source.is_file() or source.is_symlink():
            continue
        text = source.read_text(encoding="utf-8")
        document = Document()
        document.feed(text)
        document.close()
        documents[filename] = document
        errors.extend(f"{filename}: {error}" for error in document.errors)
        for tag in ("html", "head", "body", "main", "h1", "title"):
            if document.counts.get(tag) != 1:
                errors.append(f"{filename}: expected exactly one {tag}")
        if document.language != "en" or not "".join(document.title_text).strip():
            errors.append(f"{filename}: missing language or title")
        if document.current_links != 1 or "main" not in document.ids:
            errors.append(f"{filename}: current navigation and main landmark must be explicit")
        if DEVELOPER not in text or f"mailto:{CONTACT}" not in document.references:
            errors.append(f"{filename}: approved developer/contact details are missing")
        required_policy = {"default-src 'none'", "style-src 'self'", "font-src 'self'", "img-src 'self'", "base-uri 'none'", "form-action 'none'"}
        if set(part.strip() for part in document.metadata.get("content-security-policy", "").split(";")) != required_policy:
            errors.append(f"{filename}: unexpected Content Security Policy")
        if document.metadata.get("referrer") != "no-referrer" or "width=device-width" not in document.metadata.get("viewport", ""):
            errors.append(f"{filename}: missing responsive/referrer metadata")

    for filename, document in documents.items():
        for reference in document.references:
            parsed = urlsplit(reference)
            if parsed.scheme:
                if parsed.scheme == "mailto":
                    if reference != f"mailto:{CONTACT}":
                        errors.append(f"{filename}: unexpected mail destination")
                elif parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password:
                    errors.append(f"{filename}: unsafe external reference")
                continue
            if parsed.netloc or parsed.query:
                errors.append(f"{filename}: unexpected relative URL authority/query")
                continue
            relative = unquote(parsed.path) or filename
            target = (SITE / relative).resolve()
            if not target.is_relative_to(SITE.resolve()) or relative not in EXPECTED or not target.is_file():
                errors.append(f"{filename}: missing or outside-root local reference: {relative}")
            elif parsed.fragment and (relative not in documents or parsed.fragment not in documents[relative].ids):
                errors.append(f"{filename}: missing anchor: {reference}")

    for public_name, original_name in ORIGINAL_ASSETS.items():
        public, original = SITE / public_name, REPOSITORY / original_name
        if public.is_file() and original.is_file():
            if hashlib.sha256(public.read_bytes()).digest() != hashlib.sha256(original.read_bytes()).digest():
                errors.append(f"Bundled asset does not match its original: {public_name}")
        else:
            errors.append(f"Original asset is missing: {original_name}")
    return errors


if __name__ == "__main__":
    try:
        failures = validate()
    except (OSError, UnicodeError, ValueError) as error:
        failures = [f"Cannot validate public site: {error}"]
    if failures:
        print("Public site validation failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        sys.exit(1)
    print(f"Public site validated: {len(EXPECTED)} allowed files, 3 pages, local references and original assets intact.")
