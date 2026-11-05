# DeskWing high-refresh security review — 2026-11-05

Final result: **no new Snyk findings**. The final root scan succeeded at **23:23:36 Europe/Istanbul** with seven pre-existing Low findings, zero Medium and zero High. Both separately scanned generated Media Foundation probes reported zero issues. No suppressions were added.

## Scope and actual scanner

Scanned `F:/streamer-board-high-refresh` after the pen-preview/main merge and final fixes: source base `b4b2bbd` plus the reviewed Android startup watchdog, fixed native-test helper selection, iOS availability guard, runner updates, and exact `PenPreview.cs` port from main `ae72437`. Source was frozen during the final scan; documentation edits do not alter that source snapshot.

The official cached Snyk CLI **1.1308.3** hosted `snyk mcp -t stdio`. JSON-RPC `tools/call` invoked the actual **`snyk_code_scan`** with each absolute directory path. No authentication or trust settings were changed. The desktop catalog still did not expose a direct Snyk connector. Root exclusions remain those in `.dcignore`; generated probes under `.test-output` therefore received explicit separate calls. The scanner does not provide per-file/per-language coverage counts or compile native clients.

| Check | Result and local evidence |
| --- | --- |
| Initial high-refresh root scan | 8 Low, 0 Medium/High; `.test-output/snyk-high-refresh-final-2026-11-05.json` |
| New scanner finding | `csharp/CommandInjection/test`, CLI argument flowing from `tests/display-stream/Program.cs` to the optional native probe's executable selection |
| Remediation | Removed arbitrary executable arguments. Only `--native-capture` is accepted; the helper is fixed to the known project output using a compiler-provided source location, with no CLI/environment/current-directory executable selection |
| Final root rescan | 7 pre-existing Low, 0 Medium/High; `.test-output/snyk-high-refresh-rescan-2026-11-05.json`; new finding disappeared |
| Explicit `mf-interop-probe` directory scan | 0 issues; `.test-output/snyk-mf-interop-probe-2026-11-05.json` |
| Explicit `mf-rcw-probe` directory scan | 0 issues; `.test-output/snyk-mf-rcw-probe-2026-11-05.json` |
| Main deadline fix scan | Main `ae72437` also returned 7 pre-existing Low, 0 Medium/High at 23:21:41; main artifact `.test-output/snyk-deskwing-mcp-preview-deadline-final-2026-11-05.json` |

## Manual findings fixed and rechecked

- iOS: bounded timestamps for combined configuration/video packets before signed conversion; corrected late-start transport retirement and host teardown; actual-picture activity accounting rather than configuration-flag accounting.
- Android: validated actual SPS dimensions and matching configuration before decoder creation and on later units; classified actual VCL content so configuration markers cannot bypass video timestamp checks.
- Android bridge: bounded monotonic native tickets isolate original JavaScript IDs across reloads. Duplicate/early errors cannot settle another pending request; ticket consumption and document checks occur at UI delivery. Local mutations and network commits retain the original document generation, and late input-lease cleanup still runs.
- Android startup: an independent five-second watchdog covers partial header/access-unit reads and disconnects only its exact pending connection. Acceptance/expiry is atomic; heartbeats cannot satisfy or extend first-picture startup. This closes the per-read-timeout trickle gap found during final readback.
- Main pen preview: monotonic elapsed-time checks discard a late native capture even if a timer callback is delayed. Temporary busy retries retain only the same session's image for at most 1,500 ms from its original request; hard errors and lease/target/lifecycle changes still clear immediately.

Independent reviewers rechecked the repaired native and backend boundaries. No unresolved concrete manual finding remains in the final scanned source.

## Boundaries verified

The dedicated HTTPS video channel retains immutable certificate pinning and bearer authentication, exact owner/lease start-stream-stop semantics, ongoing authorization, and revocation/shutdown cancellation. It captures only the app-owned independent virtual display after exact identity, permission, generation, bounds and topology checks. DXGI cannot switch to an unrelated output; the 60 Hz GDI fallback retains the same authorized target.

Capture/encoding stays on one worker with bounded buffers and pending-write retention. DIB/DXGI frame, map, COM, bitmap and cursor resources have checked lifetimes. Cursor composition is clipped to the selected monitor. Native packet/decoder queues, SPS dimensions and timestamps are bounded. Heartbeat flag 8 is exclusive and payload-free; it does not enter video decoding or FPS counts. Background, navigation, renderer, pairing, replacement and teardown paths stop the exact session. Merged pen-preview transport and stale-image guards remain intact.

Owners reported the earlier full 64-suite integration passing and a fresh publish with **all nine affected suites passing** after the final deadline/runner fixes. The helper remediation also passed 65 display and 68 stream checks. Security reviewers performed source review and scans, not physical capture/input or Mac compilation; build, device behavior and measured refresh performance remain recorded by their respective verification owners.

## Dependencies and existing findings

No external runtime dependency was added for encoding/capture; Windows Media Foundation/DXGI/GDI are used. Existing clean dependency audits remain applicable: `npm audit --prefix tests --json` reported zero vulnerabilities across six dependencies, and `dotnet list companion/TabletDeck.csproj package --vulnerable --include-transitive --format json` reported no vulnerable packages. Inputs were rehashed after final changes:

- `tests/package-lock.json`: `3292BABE92912B8AF34928340384C13E2C92D5F68C334C5048B6AA56F5F8C2C6`
- `companion/TabletDeck.csproj`: `7888DD9E8FE3D753C05B5E6D1F9A5A19D5BD6F26F836D3EB4E452D5129665870`

The remaining seven Low findings match the documented baseline: JavaScript-enabled WebViews in `ChatPane.java:28` and `MainActivity.java:97`, localhost HTTP in `tests/computer-api.cjs:40`, and existing isolated test TLS configuration in `execution.cjs:11`, `features.cjs:13`, `headless.cjs:35`, and `integration.cjs:22`. This report does not claim an issue-free repository. See [the prior review](SECURITY-REVIEW-2026-11-05.md) for baseline context and dependency audit artifacts.

All scan artifacts are ignored local evidence, not release contents. Subsequent first-party source changes require a bounded follow-up scan.
