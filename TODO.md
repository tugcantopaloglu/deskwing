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
- [x] Refine tablet fullscreen to hide Android system bars and use a canvas-only surface with compact reachable controls.
- [x] Add a separate resizable PC Ink window and permit a smaller resizable main companion window.
- [x] Refresh Pen tablet target dimensions after PC resizing and apply exact session geometry before accepting pen input.
- [x] Reduce pen batching delay and fix the diagnostic canvas's edge alignment and repeated repaint cost.
- [x] Show the selected PC target live behind Pen tablet input so drawings are visible on the tablet.
- [x] Prevent transient preview throttling from flashing the image or repeatedly opening fullscreen tools.

Expanded pen evidence: shared ink passed 50 backend and 38 HTTP checks; native Windows receiver confirmed real position, pressure, tilt and clean release; 56 two-client/fullscreen browser checks passed. Independent visual review passed all 16 final captures. Microsoft retired standalone Whiteboard; physical testing uses the local diagnostic canvas unless a supported web/Teams account is available.

Physical pen checkpoint: user confirmed pressure and fullscreen operation after the signed update; the PC received real Xiaomi pen strokes with changing pressure and tilt. User requested a further immersive fullscreen refinement and resizable PC windows. The 55-suite local aggregate passed; iOS build and 8 simulator tests passed for b683825. Final security rescan had zero new findings (7 pre-existing Low). Hosted Windows passed 54/55 suites; a cold-start timeout in a certificate-export test helper was raised, with focused ink/pen API checks still passing.

Previous checkpoint: commit 0691c1a passed all 57 Windows CI suites and 9 iOS tests. User confirmed the drawing delay was fixed and then requested a live view of PC drawings on the tablet.

Live pen-preview implementation: all ten affected suites passed against a fresh Windows publish, including 42 fake capture checks, 49 frontend checks and 153 rendered checks. Native own-window capture reproduced changed strokes and exact client edges at two sizes. Seven final visual captures passed review; the actual Snyk scan found zero new issues. Commit a9a85c9 passed all 58 Windows CI suites and 11 iOS tests. The signed Android update and matching Windows companion were installed with pairing/data retained. The user confirmed seeing PC drawings on the tablet, then reported frequent refreshing and fullscreen instability.

Pen-preview correction: reproduced the client/server 84 ms scheduling collision, which cleared the image and reopened tools on brief throttling errors. The correction adds pacing margin, retains only a fresh same-session image for at most 1.5 seconds, and prevents transient errors reopening tools. All 58 controller and 155 rendered checks passed; a fresh actual Snyk scan found no new findings. The signed correction is installed; physical retest is pending.

## 9. High refresh Second display (added by user)
- [ ] Replace the four-frame-per-second JPEG view with an actual video capture, transport and playback path targeting at least 60 fps.
- [ ] Add 60 Hz and Match tablet refresh rate options using reported device capabilities, with the chosen rate applied to the virtual monitor and stream.
- [ ] Show measured delivery/playback performance and verify the connected Xiaomi tablet, including stop, background, reconnect and sharing revocation.
- [ ] Validate Windows, Android and iOS implementations, rerun security/CI and update packages and documentation.
