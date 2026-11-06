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
- [x] Validate expanded pen modes, update packages, rerun security/regressions and document verified app compatibility and remaining app-specific limits.
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

Pen-preview correction: reproduced the client/server 84 ms scheduling collision, which cleared the image and reopened tools on brief throttling errors. The correction adds pacing margin, retains only a fresh same-session image for at most 1.5 seconds, and prevents transient errors reopening tools. All 58 controller and 155 rendered checks passed; a fresh actual Snyk scan found no new findings. The user confirmed that the installed pen tablet, image and fullscreen are stable. The final monotonic capture-deadline correction passed a fresh complete 58-suite run and its actual security scan.

## 9. High refresh Second display (resumed 2026-11-06)
- [x] Implement native capture, H.264 transport, decoder startup/recovery, and 60 Hz/Match tablet selection with automated coverage.
- [x] Fix the Android launch crash and finish bounded off-main-thread network cleanup, with lifecycle and stale-callback regressions; confirm the installed app opens without the prior exception.
- [x] Pass 65 Windows suites and 39 iOS tests for candidate `002c3d5`, rerun security review, and verify original-signer Android packages.
- [x] Confirm final hosted CI for `9989c86`: 65 Windows suites, including 78 metric checks, and 39 iOS tests passed.
- [x] Rehearse 60 Hz on the connected Xiaomi tablet: the user confirmed the desktop appears and motion is smooth; received rate was 59.87–59.88 fps with matching render counts and zero reported drops.
- [x] Correct the shown-FPS observation clock, pass 78 metric checks and SDK 36 compilation, rerun the actual Snyk scan, and install the signed metrics build.
- [x] Verify the corrected shown-FPS counter on the tablet and confirm Home/reopen/explicit restart without a crash.
- [x] Rehearse Match tablet at the reported 120 Hz: the user confirmed smooth motion, with 118.72–119.80 received fps and zero reported drops during the bounded moving-cursor test.
- [x] Exit the 60 Hz viewer and start Match tablet at 120 Hz without an app crash.
- [x] Revoke sharing during live video and verify all native video surfaces disappear without an app crash.
- [x] Remove the owned test display and confirm physical monitor names, positions, and sizes are identical; recreate a shared display for continued normal use.
- [x] Record the observed Home/reopen, restart, sharing-revocation and display-removal results with their bounded performance limits.
- [ ] Separately rehearse exhaustive USB/Wi-Fi interruption, latency and load cases before making broader reliability or performance claims.

Candidate `002c3d5` passed all **65 Windows suites locally and in hosted CI**, plus **39 iOS simulator tests**. The later metrics-only fix `9989c86` passed **78 metric checks**, a complete **SDK 36 / Java 8 compile**, and [final hosted CI](https://github.com/tugcantopaloglu/deskwing/actions/runs/42252632169) with **65 Windows suites and 39 iOS tests**. Both actual security checkpoints retained seven pre-existing Low findings and found no new issues; see [the resumed-work security report](docs/SECURITY-DISPLAY-RESUME-2026-11-06.md). Original-signer packages were verified, the updated universal APK is installed, and Android opens without the historical `NetworkOnMainThreadException`.

The Windows companion was deployed from the normal same-user context, outside Codex's packaged host, against the real data directory. Four existing private files retained their hashes across deployment, and the repaired six-page board was retained. The user confirmed smooth 60 Hz and Match tablet viewing. Motion samples measured 59.87–59.88 received fps at 60 Hz and 118.72–119.80 received fps at a 120 Hz target, with render counts keeping pace and zero reported drops. Static content correctly entered Still image without artificial frames, and the 60 Hz → Exit → 120 Hz start sequence worked without a crash. These were bounded moving-cursor checks, not an end-to-end latency study.

The earlier shown-FPS estimate was incorrectly zero because freshness compared different clock origins. After installing `9989c86`, the user confirmed “Works, and FPS counter rises” for Match tablet motion and Home/reopen/start. Final samples reported a decoder-rendered estimate of 120.10 fps alongside 120.69 and 119.83 received fps, with zero reported drops. A new 60 Hz session after Home/reopen advanced render counts without an AndroidRuntime error; the app stayed alive. These are decoder callback estimates and bounded device observations, not camera-measured display cadence or certified latency/load performance.

Sharing revocation was exercised during live video: the three DeskWing SurfaceView layers dropped to zero while the app stayed alive, with no AndroidRuntime error. The owned `DISPLAY5` was removed and both physical monitors' names, positions, and sizes matched their prior state exactly. A new owned display at 1280×800 and 165 Hz was then created with sharing enabled and intentionally left available for normal continued use. It is not an abandoned temporary test monitor.

The native path uses DXGI GPU capture, software Media Foundation H.264 encoding, separate pinned HTTPS video transport, and native decoding. Earlier isolated PC-side capture/encoding/framing measured 60.01 fps at a 60 Hz target and 118.75 fps at a 120 Hz target. Those measurements exclude network delivery and tablet playback. That earlier test removed its owned monitor without changing physical monitor topology; it does not establish end-to-end 60+ fps for this candidate.

## 10. Missing Soundpad buttons (repaired 2026-11-06)
- [x] Identify the missing live Memes page in the actual user's board and distinguish it from the packaged development host's different data view.
- [x] Append one live Memes page through the revision guard with an explicit backup, preserve all original pages/buttons, and restore the intended Soundpad output.
- [x] Verify the connected tablet: the user confirmed that the buttons are visible and audio works.

The actual board contained five pages and 21 buttons but no Memes page; the old Codex-virtualized data view showed eight pages and was not the user's active board. The repair added one live Memes page and compared the original five pages/buttons after saving to confirm they were identical. Its initial microphone-only output was corrected to **both microphone and speakers**, matching the original intended setting. The user confirmed visible buttons and working audio. No Soundpad product-code change was required, and this confirmation does not certify audio routing in every receiving application. See [the deployment-context note](docs/BUILDING.md#development-host-and-real-user-data).
