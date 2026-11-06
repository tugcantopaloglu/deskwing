# DeskWing resumed display security checkpoint — 2026-11-06

The actual **`snyk_code_scan`** completed successfully on the resumed high-refresh worktree. Verification at **19:12:19 Europe/Istanbul** found **seven unchanged Low findings, zero Medium/High findings, and no new findings**. No suppression, authentication, trust, or dependency configuration was changed.

Latest source checkpoint: the playback-metrics correction at **`9989c864c1eb3fc3befff0654f7de45dddb26c4b`** passed a fresh actual scan, verified at **19:29:34 Europe/Istanbul**, with the same seven Low findings and no new findings. The focused follow-up is recorded below.

This is a source-security checkpoint. It does not establish physical tablet frame rate, native decoder compatibility on every device, or app-store readiness. Device rehearsal and the current hosted build/test results are separate evidence.

## Scanner and frozen scope

The official cached Snyk npm wrapper hosted **Snyk MCP Server 1.1308.3**, with `mcp -t stdio`. JSON-RPC `tools/call` invoked the actual `snyk_code_scan` against `F:/streamer-board-high-refresh`. The executable used was the cached package's `wrapper_dist/snyk-win.exe`; the separate smaller standalone executable does not host the MCP command. No substitute CLI-only scan was represented as a tool invocation.

The source base was **`21599d453930cde0ac802d07670e9a29a9c22a3b`**, including the merge of stable main and the DeskWing GitHub rename, plus the frozen resumed changes:

- Android `FastLink`, `MainActivity`, `NativeDisplayVideo`, `NetworkCloseQueue`, `DisplayReadDeadline`, and new `DisplayDecoderStartup`.
- Android fast-link and new display-lifecycle harnesses, plus their explicit regression-runner entry.
- The shared display refresh UI and Copilot UI regressions.
- The iOS display recovery controller and its regression cases.

The scanner used the existing `.dcignore`. A separate before/after snapshot hashed **206 source/dependency inputs**, with **zero changes** during the scan. This immutability check is not a claim of per-file or per-language scanner coverage; the scanner does not report those counts. Ignored generated artifacts were not represented as scanned production code.

## Manual findings repaired and rechecked

- **Android UI-thread TLS cleanup:** resets, pauses, navigation, and teardown detach the exact connection and close it on a bounded worker. Reconnection cannot overtake an unfinished old close. Cleanup exceptions cannot strand the display's admission flag or suppress later cleanup.
- **Connection/document ownership:** queued fast state and closure callbacks verify original document, lifecycle, and connection generation when delivered. Local forget/transport/pairing changes recheck the original scope at commit time under the connection lock; an old pairing response cannot overwrite a replacement connection.
- **Immutable endpoint credentials:** each queued HTTPS operation captures host, port, certificate pin, and token together. Exact safety cleanup retains its original endpoint rather than borrowing a replacement connection's credentials.
- **Late ordinary responses:** API results are checked again on the UI thread against the submitted lifecycle. A result from an old PC connection or workspace is rejected instead of replacing the current board/state. Local pairing replies remain valid after their intentional epoch change. Rejected results do not replay actions.
- **Android first-picture startup:** the first IDR is retained while decoder input buffers become ready. A separate monotonic playback deadline requires an actual accepted frame-render callback before the start result reports success. Timer delay, heartbeats, and repeated buffer polls cannot extend the deadline.
- **iOS recovery:** first-picture and decoder-recovery waits have absolute five-second budgets. Heartbeats and repeated resets do not renew them. Same-session image retention is bounded by recovery; stop or revocation removes the image immediately.
- **Refresh-rate honesty:** a reported nominal tablet cadence below 60 Hz disables Match tablet and blocks direct start. It is not silently relabeled as a 60 Hz match; the explicit 60 Hz option remains distinct.

The existing pinned TLS, exact display-owner/lease checks, packet bounds, image dimensions, timestamp validation, and Windows capture-target restrictions remain in place. No playback, pen injection, or user application control was performed by the security reviewers.

## Soundpad repair scope

The missing Memes page was a live configuration issue, with no Soundpad product-code change. The manually reviewed PowerShell helper used the actual same-user companion context, a fixed loopback endpoint, the current profile/revision, an explicit backup, and a single live-page append. It kept the admin token in request headers and produced only repair metadata. Existing pages were compared after the save; the revision-aware server route preserves connection settings, credentials, favourites, and other profiles.

The root agent reported all five original pages and 21 buttons unchanged, the added page exposing 1,421 Soundpad entries, and the user's confirmation that Memes and its buttons were visible. Local evidence is `F:/streamer-board/.test-output/resume-20261106/memes-repair-result.json`. PowerShell is not listed as supported by this Snyk Code tool, so the helper received manual review, not claimed SAST coverage.

## Validation and remaining limits

The Android owner reported a complete SDK 36 / Java 8 source compilation, 33 fast API checks plus 44 native fast-link checks, 23 lifecycle checks, and 273 protocol checks. Unified focused evidence is under `.test-output/verification-rbOpGr` and `.test-output/verification-HxCWtB`. The frontend owner reported 83 Copilot UI checks passing. These are focused checks, not a claimed final full regression run.

Four new iOS regression cases cover inbox overflow followed by heartbeats, repeated resets and late IDR arrival, successful recovery followed by a still image and new recovery, and the initial absolute deadline. The Windows review host cannot execute Xcode or XCTest; compilation/test execution requires the current hosted Mac run. Prior green iOS runs do not certify these new tests.

Dependency inputs remain identical to the prior audited inputs:

| Input | SHA-256 |
| --- | --- |
| `tests/package-lock.json` | `3292BABE92912B8AF34928340384C13E2C92D5F68C334C5048B6AA56F5F8C2C6` |
| `companion/TabletDeck.csproj` | `7888DD9E8FE3D753C05B5E6D1F9A5A19D5BD6F26F836D3EB4E452D5129665870` |

The seven remaining Low findings match the previous rule/path/severity baseline: JavaScript-enabled WebViews in `ChatPane.java:28` and `MainActivity.java:97`; the loopback HTTP test in `tests/computer-api.cjs:40`; and existing isolated self-signed TLS fixtures in `execution.cjs:11`, `features.cjs:13`, `headless.cjs:35`, and `integration.cjs:22`. See the [previous high-refresh review](SECURITY-HIGH-REFRESH-2026-11-05.md) and [stable review](SECURITY-REVIEW-2026-11-05.md) for context. This does not claim an issue-free repository or a fresh dependency-audit run.

Local ignored evidence:

- `.test-output/snyk-display-resume-final-2026-11-06.json` — actual MCP response.
- `.test-output/snyk-resume-source-snapshot-2026-11-06.json` — before-scan hashes.
- `.test-output/snyk-resume-verification-2026-11-06.json` — baseline comparison and after-scan immutability check.

Subsequent supported first-party source changes require a follow-up scan. Documentation-only changes do not alter this source checkpoint.

## Playback-metrics follow-up

Physical testing exposed a reporting defect: received and rendered counters advanced during smooth 60/120 Hz playback, but the shown-FPS estimate was zero because frame freshness compared different clock origins. The root agent reported approximately 59.88 and 119.80 received fps, rendered counts keeping up, zero drops, and the user's smooth-playback confirmation. Those are physical-test reports, not measurements performed by this security review.

Commit `9989c864c1eb3fc3befff0654f7de45dddb26c4b` changes only `RenderedFrameMetrics.java`, the numeric diagnostic log in `NativeDisplayVideo.java`, and `RenderedFrameMetricsHarness.java`. Cadence still derives from render-time intervals in a bounded 512-entry/two-second window. Freshness now uses the last accepted callback's local observation time; duplicate/regressing samples cannot refresh activity, and inactive measurements expire. The new diagnostic fields contain only numeric clock/window/age values. The manual review found no new endpoint, credential, authorization, decoding, or playback-pipeline change.

The Android owner reported **78 metric checks passing** and complete SDK 36 / Java 8 compilation after this change. Local focused evidence is `.test-output/frame-metrics-e79cc046193d4457832c4b929c5b0b68`. Correct on-device shown-FPS behavior after installing this exact metrics build remains a separate rehearsal result.

The actual `snyk_code_scan` re-ran on the entire frozen high-refresh root through the same official MCP server. All **206 captured source/dependency inputs** were unchanged before/after the scan, both dependency hashes above remained unchanged, and the finding rule/path/severity set matched the previous seven-Low baseline exactly. No remediation or suppression was needed. The scanner process then closed cleanly.

- `.test-output/snyk-display-metrics-final-2026-11-06.json` — actual MCP follow-up response.
- `.test-output/snyk-metrics-source-snapshot-2026-11-06.json` — frozen input hashes at `9989c86`.
- `.test-output/snyk-metrics-verification-2026-11-06.json` — unchanged input and zero-new-finding verification.
