# App development checklist

Requested 2026-11-05: implement every inspection finding, improve the design, and add Xiaomi tablet/stylus workflows. Check items only after implementation and relevant verification. Hardware-only checks remain explicitly identified.

## 1. Foundations and design
- [x] Record the product/design direction and regroup desktop navigation.
- [x] Rename visible app branding while retaining installation, pairing and configuration compatibility (DeskWing, selected by user).
- [x] Protect board saves with revisions and isolate board edits from connection settings.

## 2. Board creation
- [x] Make the PC editor a visual board with page navigation and selected-button inspector.
- [x] Add duplicate/copy/move buttons and pages, including cross-page/profile reuse.
- [x] Add multi-select styling, undo/redo and recoverable local drafts.
- [x] Replace generic macro fields with typed steps, pickers, reorder/duplicate and duration summary.

## 3. Tablet operation
- [x] Put Soundpad All/Favourites/Recent in the live pad.
- [x] Add Copilot Follow app / Pin layout controls on the tablet.
- [x] Add pen-aware controls, precise fader/arrangement input and palm protection.
- [x] Add a persistent drawing scratchpad with pressure support, undo/redo, clear and export.
- [x] Add handwriting entry using supported Android IME facilities and a clear text fallback.

## 4. Readiness and recovery
- [x] Add a profile readiness view with actionable missing-target/dependency checks.
- [x] Add restore-as-new-profile and clear backup differences before restoring.
- [x] Recover damaged auxiliary settings safely; fail closed for damaged pairings.

## 5. Reliability
- [x] Coordinate held keys and shortcut/macro ownership without blocking harmless audio controls.
- [x] Apply one action policy to HTTPS/fast TLS and Android background lifecycle; bound queued writes.
- [x] Isolate slow media session providers and keep healthy session identities.
- [x] Correct timeout disconnection accounting.

## 6. Verification and delivery
- [x] Add a complete regression entry point and Windows CI coverage.
- [x] Run relevant existing and new regression suites and build both components.
- [x] Run required Snyk scan when its tool is available; record any concrete tooling blocker.
- [x] Inspect desktop/tablet layouts, pen/scratchpad states and accessibility; fix material findings.
- [x] Document changes, compatibility, hardware rehearsal procedure and actual verification limits.
- [ ] Rehearse physical Xiaomi stylus, handwriting IME, USB/Wi-Fi interruptions, held keys and concurrent media when hardware is available.

## Verified checkpoint
- 45 complete Windows/backend/Java/browser regression suites passed, zero skipped (2026-11-05).
- Original Android signing key recovered and certificate matched; signed APK/AAB produced. DeskWing1.14/code17 installed with data retained.
- Existing Xiaomi pairing reconnected to updated Windows companion. User confirmed pen drawing with palm resting and handwriting work.
- Actual Snyk code scan: no new findings; seven pre-existing Low notes. npm/NuGet audits clean.

## 7. Store and iOS preparation
- [x] Add SDK36 Android APK/AAB packaging with original-signer verification.
- [x] Prepare secure iOS host, shared-asset sync, XCTest and macOS CI.
- [x] Run hosted macOS build/tests and resolve compilation/runtime issues (unsigned iOS simulator job passed for e56f21e on hosted macOS).
- [ ] Finish privacy/support/store assets, signing and device rehearsal before any store submission.

## 8. Expanded pen workflows (added by user)
- [x] Mirror scratchpad ink and notes to the PC with local backup, revision conflicts and reconnection handling.
- [x] Add native Windows pen input for Windows Ink apps, with pressure/tilt and explicit PC target/permission (Whiteboard web/Teams and Photoshop still need app-specific tests).
- [x] Add fullscreen scratchpad and fullscreen pen-tablet surface with reachable exit.
- [ ] Validate expanded pen modes, update packages, rerun security/regressions and document verified app compatibility.

Expanded pen evidence: shared ink passed 50 backend and 38 HTTP checks; native Windows receiver confirmed real position, pressure, tilt and clean release; 56 two-client/fullscreen browser checks passed. Independent visual review passed all 16 final captures. Microsoft retired standalone Whiteboard; physical testing uses the local diagnostic canvas unless a supported web/Teams account is available.
