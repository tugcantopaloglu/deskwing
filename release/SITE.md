# DeskWing public beta pages

This directory is a static, self-contained overview, support guide, and privacy policy prepared for beta review. The owner confirmed the public developer identity **Tuğcan Topaloğlu** and contact **tugcan@tugcan.dev**. No email has been sent. No hosting, deployment, store submission, or public availability has been enabled by adding these files.

## Local preview

Serve `release/site` using a local static HTTP server. It has no JavaScript build step, analytics, remote fonts, form submission, or embedded provider content. The bundled Manrope font and original DeskWing SVG icon use the repository's existing assets; the font license is in `release/site/assets/OFL.txt`.

Run `python scripts/validate-public-site.py` from the repository before packaging or publishing. It allows only eight known public files, validates local links/anchors and security metadata, and compares the bundled assets with their repository originals. It performs no external fetch or publication. This guide deliberately sits outside the public payload.

## Intended hosting

If the owner enables a GitHub Pages project site for `tugcantopaloglu/deskwing`, its default base URL would be `https://tugcantopaloglu.github.io/deskwing/`, with `privacy.html` and `support.html` below it. These URLs are a deployment plan, not a claim that they are live. Relative links work below that repository path. The `.nojekyll` file keeps the site static.

The pages include `noindex, nofollow` while they are prepared for review. Remove that tag only when the reviewed site is intentionally published. GitHub Pages hosting records visitor IP addresses for security; the privacy page links GitHub's current disclosure.

## Publication gates

- Use the owner-confirmed developer identity and support/privacy contact consistently in the store listings and policy; recheck them if the publishing entity changes.
- Review the final shipping feature and data inventory, including new notebooks/PDF handling, input forwarding, file retention/history, diagnostics, optional providers, and export destinations. Conditional feature descriptions in the draft policy are not store disclosure answers.
- Update the visible beta-availability wording only after the final build and distribution status are confirmed.
- Recheck the final Android Data safety and Apple App Privacy answers from the actual artifacts. Local transfer to a paired PC and third-party provider traffic must not be dismissed as “no data collected” without that review.
- Verify the exact live URLs over HTTPS after publishing before adding them to store listings or claiming a hosted policy exists.

## Privacy decisions reflected here

Core pairing and control use a selected local PC; there is no required DeskWing cloud account. The PC receives board/action state, optional captured pixels, authorized input samples, and connected shared document content. Pairings, private files, and app settings persist on their respective devices. Drawings, document history, local recovery, and exports have separate copies/retention. Keyboard/IME, optional provider viewers, GitHub updates, mobile stores, and user-selected share destinations have their own privacy rules. Support sharing is user-initiated and must be redacted; no diagnostics auto-upload is promised or introduced.

Sources reviewed 6 November 2026: current repository `PRODUCT.md`, Android pairing/preferences and backup manifest, iOS `PairingStore.swift`, shared ink persistence/sync/export code, and [GitHub Pages data collection](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages#data-collection). This is a release review record, not legal classification or completed store disclosure.

## Recorded page validation

On 6 November 2026, the static validator passed the eight-file payload, links/anchors, metadata, developer/contact details, and bundled-asset provenance. Negative fixtures confirmed rejection of an extra private/log file, an inline script, a broken local link, and a changed original asset.

Headless Chromium checks passed all three pages at 1280, 820, and 390 pixels wide: no horizontal overflow, broken local resources, duplicate IDs, missing anchors, console errors, or scripts; one main/H1/current navigation per page; keyboard skip navigation and 44px main-navigation targets. A confirmation pass checked the corrected skip-link and primary-button hover contrast. Desktop and phone captures were inspected against the existing dark/cyan Manrope world. The independent content review's QR-camera omission was corrected and scored resolved.

These checks cover the static pages only. They do not publish the site, validate store declarations, or certify the app's device behavior. The first-party Python validator belongs in the final code security scan with the other release changes.
