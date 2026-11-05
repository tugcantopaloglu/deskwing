# DeskWing security review — 2026-11-05

Status: integrated, fullscreen/window, pen-feedback, and lease-gated live-preview scans and required remediations are complete for `F:/streamer-board`. The latest actual `snyk_code_scan` reports no new findings: seven pre-existing Low findings, zero Medium, and zero High. No scanner suppressions were added. Dependency inputs remain identical to the completed clean dependency audits. The separate `F:/streamer-board-high-refresh` worktree is outside this scan scope.

## Scanner availability and method

The desktop tool catalog did not expose `snyk_code_scan`, and plugin discovery returned no Snyk plugin. The previously installed official Snyk CLI 1.1308.3 was available in the local npm cache. Its documented MCP mode exposes the actual `snyk_code_scan` tool.

The review initialized `snyk mcp -t stdio` over a local stdio connection, listed tools, and called `snyk_code_scan` with `{ "path": "F:\\streamer-board" }`. No authentication or folder-trust settings were changed, no credentials were printed, and no source-executing scan was used. This is a call to the official MCP scanner, not a claim that the missing desktop connector became available.

Official references: [Snyk MCP cheat sheet](https://snyk.io/articles/snyk-mcp-cheat-sheet/) and [Snyk Studio MCP repository](https://github.com/snyk/studio-mcp).

## Baseline

- The actual MCP call completed successfully and reported seven Low issues, with no Medium or High issues.
- These match the previous 1.13 release categories: two WebView JavaScript-enabled findings and five localhost HTTP / test TLS configuration findings.
- The existing JavaScript-enabled findings occur in the bundled Android interface and the separately isolated provider-chat view. Their presence is documented; JavaScript remains required for the application. This review does not describe the scanner as completely clean.
- The five test findings concern isolated test fixtures, not production tablet certificate validation.
- The baseline CLI scan also reproduced the same seven findings (exit 1 because findings exist).
- Baseline evidence: `.test-output/snyk-deskwing-mcp-baseline-2026-11-05.json` and `.test-output/snyk-deskwing-baseline-2026-11-05.json` (ignored local artifacts).

## Manual checks during implementation

The new native pen routes use bounded text/PNG payloads. PNG export validates its signature and dimensions and writes only to an Android document destination selected by the user. Handwriting uses a native text field and the installed Android input method. Board-draft persistence copies editable board fields rather than OBS credentials or complete configuration objects.

The app does not add a remote handwriting-recognition service. The installed keyboard's own privacy policy and processing behavior still apply; handwriting recognition must not be advertised as guaranteed offline merely because the app adds no remote OCR.

The iOS source uses a bundled-page/main-frame bridge gate, exact route allowlists, a private numeric PC address, immutable SHA-256 certificate pins verified before credentials are sent, bounded responses, no redirects, and a device-only Keychain item. A queued-write concern was corrected with a two-second deadline for controls whose request body has not started; pen writes use a shorter half-second deadline. Release/cancel requests are exempt, long macro responses are not cut off merely for taking time, and ambiguous on-wire races are never retried. This security review is source-only; macOS compilation, Apple trust-stack behavior, URLSession timing, and physical-device validation are separate checks recorded by their build/device owners.

The Android packaging scripts confine proto-resource extraction to their new module directory, verify pinned tool hashes before execution, keep development signing/package identity separate, pass signing passwords by file, and refuse missing release keys. These PowerShell scripts were manually reviewed; PowerShell is not among the supported languages listed by the Snyk Code tool. The installer shortcut migration compares full canonical targets and preserves unrelated shortcuts.

The expanded shared-ink service accepts only the documented JSON fields, finite bounded stroke coordinates, and a bounded notes document. Authenticated writes use a revision compare-and-swap and a private temporary file followed by atomic replacement. Invalid existing data is quarantined under a generated local name. Polling accepts one exact revision query; it does not expose board credentials or executable targets.

An independent source review of native Windows pen input found no actionable new issue in the PC-local enable/target consent, authenticated paired-device ownership, exact-lease release, sequence/age limits, focus/geometry cancellation, pointer cleanup, keyboard/update exclusion, or Android/iOS suspension handling. Coordinate conversion follows the [Microsoft synthetic-pointer API specification](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput). This security review did not inject input; the separate verification owner reported that the opt-in, own-window native receiver test passed. That receiver source is included in the root scan scope.

The follow-up fullscreen controls stay inside the existing trusted local bridge. Entering fullscreen requires a foreground workspace and a Boolean request; lifecycle changes and native tools restore normal presentation, and exit remains available. The separate PC ink window requires local administrator authentication, navigates to a fixed local management document, and keeps WebView2 host objects and web-message bridges disabled. The administrator credential remains in the URL fragment until the existing page removes it, and window close/shutdown disposes the view. Export changes retain the allowed local/blob origin check and user save dialog; PNG/TXT filters do not introduce an arbitrary launch URL, executable path, or silent filesystem destination. These checks found no actionable new security issue.

A shutdown runtime check exposed an admission gap during HTTP graceful shutdown. The corrected `BeginShutdown` closes admission under the same gate as pen samples, renewals, held keys, and action starts before releasing active input. Late releases remain accepted, and completing maintenance cannot reopen shutdown admission. An independent read-only review found no remaining actionable admission, cleanup, or lock-order issue in that change; the owner reported 33 pen-integration and 27 reliability checks passing.

The final pen-responsiveness/resize follow-up preserves these boundaries. Idle target-size refresh cannot start or renew input, begin returns the exact resolved lease dimensions, and resizing an active target still invalidates the lease before additional samples and requires an explicit restart. The frontend applies target geometry before activating the session and sends first contact promptly without expanding authorization or payload limits. Independent source review found no new lease/security issue. The temporary native latency profiler only enumerates/resolves targets and measures elapsed time; it does not create or inject input. Because the normal scan excludes `.test-output`, its directory received a separate explicit scanner call.

The new live-preview source uses an authenticated, bounded `POST /api/pen/frame` route with exact active-owner/lease snapshots checked before and after capture. Capturing does not renew input or run under the pen/input gate. Window targets use client-only `PrintWindow` into a private bitmap with no desktop-copy fallback; only an explicitly selected display uses display capture. One bounded worker remains occupied until native capture finishes, even if its HTTP caller times out, so a hung target cannot accumulate capture workers. Images are size-limited JPEG data URLs without a frame cache. Independent backend review found no concrete privacy, lease, or locking defect. The frontend validates target geometry, sequence, and image format/size and rejects stale request/decode results. Native completion/lifecycle guards are included in the final review and scan.

The Android preview review identified a delayed-success delivery race after pause or lease change. The fix checks the exact active lease, foreground state, lifecycle generation, and begin/end/cancel intent revision inside the final UI callback, substituting an error-only result when stale. Its 37-case lease harness includes 15 stale-frame cases, and SDK 36 compilation passed. iOS uses a separate one-request pinned preview session and generation/lease/ticket checks; ordinary page navigation now resets bridge tickets and releases old input before callbacks can be reused. The owners' isolated native preview test captures only its own window, verifies three JPEG cases, and rejects a covering window through the focus gate before capture.

The main-app preview hotfix at `0df805c` preserves a recent image only for an exact allowlist of temporary capture/rate/queue-busy errors. Freshness remains limited to 1,500 ms from the original request start with an independent generation/sequence timer; retries do not extend it. Stop, replacement, target/lifecycle changes, malformed frames, and authorization/lease errors still clear immediately. This source review found no weakened capture or lease boundary. The rendered fixture passed 155 checks, including retry freshness expiry without opening tools and immediate hard-error clearing.

## Final verification

The root-directory `snyk_code_scan` call included the integrated C#, Android Java, JavaScript, Python and iOS Swift source under the normal `.dcignore` exclusions. The MCP response does not report a per-language file-count breakdown. The scan does not compile or execute the iOS host.

| Check | Actual result |
| --- | --- |
| Initial integrated `snyk_code_scan` | Success; 8 Low, 0 Medium, 0 High |
| Remediation | Replaced a newly introduced literal test password in `tests/reliability-api.cjs` with `crypto.randomBytes(32).toString('hex')`; retained a private exact-value persistence assertion |
| Relevant regression | Author rebuilt cleanly and ran 16 reliability API checks successfully |
| Post-fix `snyk_code_scan` | Success; 7 Low, 0 Medium, 0 High; no new findings compared with baseline |
| Follow-up after default self-test stopped performing WAV playback | Root-directory `snyk_code_scan` again succeeded with the same 7 Low / 0 Medium / 0 High findings; no new findings |
| Initial expanded shared-ink/native-Windows-pen scan | Success; 9 Low, 1 Medium, 0 High. Three newly reported warnings: Android custom trust-manager validation, a literal ink-test secret, and the ink-test HTTP upload helper |
| Expanded-scope remediations | `PinnedTrustManager` verifies the expected fingerprint and certificate validity before creating a platform trust manager anchored only to that certificate, then delegates server-chain validation. The ink fixture uses a per-run cryptographic secret. Ink and pen oversized-upload fixtures use HTTPS with their private test certificate as CA and an exact DER peer check; TLS verification remains enabled |
| Relevant expanded-scope regressions | Android SDK 36 Java compilation passed; production trust manager passed 31 fast API + 17 native Java checks with the real self-signed companion fixture, including wrong-pin and empty-chain rejection. The ink API owner reported all 38 assertions passing, including both oversized HTTPS upload cases. The pen API owner reported 18 assertions passing |
| Final expanded-scope `snyk_code_scan` | Success; 7 Low, 0 Medium, 0 High. All three newly reported warnings disappeared; no new findings compared with baseline |
| Initial fullscreen/window follow-up `snyk_code_scan` | Success; 8 Low, 0 Medium, 0 High. A literal credential in `tests/desktop.cjs` triggered one newly reported `javascript/NoHardcodedPasswords/test` warning |
| Fullscreen/window remediation and regressions | Replaced the desktop fixture credential with `crypto.randomBytes(32).toString('hex')`, preserving exact-value assertions; all 64 desktop checks passed. Owners also reported 46 Android fullscreen-policy checks, 25 compact ink-window UI checks, 94 pen-workspace checks, and the shutdown regressions documented above |
| Final fullscreen/window/shutdown `snyk_code_scan` | Success; 7 Low, 0 Medium, 0 High. The desktop fixture warning disappeared. Frozen scope included the final compact canvas layout, Android/iOS native fullscreen, PC window and export changes, and shutdown admission/cleanup; no new findings compared with baseline |
| Final help-text-only follow-up `snyk_code_scan` | Success at 2026-11-05 21:48:30 Europe/Istanbul; 7 Low, 0 Medium, 0 High, with no new findings. Root scan included the `tests/run-all.cjs` usage example correction on runtime base `702d40b`; the delta changes displayed help text only |
| Final pen-feedback `snyk_code_scan` | Root scan succeeded at 2026-11-05 22:00:02 Europe/Istanbul: 7 Low, 0 Medium, 0 High; no new findings. Scope includes geometry snapshots, frontend scheduling, manual receiver geometry/rendering, cold WebView readiness/diagnostics, and test-runner time budgets. Owners reported 52 pen-backend, 33 integration, 39 frontend, 43 rendered-desktop, and 27 native-window checks passing |
| Isolated latency-profiler `snyk_code_scan` | Explicit scan of `F:/streamer-board/.test-output/pen-resolve-latency` succeeded at 22:01:04: 0 issues. This additional call covers the generated C# profiler outside the normal root scan exclusions |
| Final live-pen-preview `snyk_code_scan` | Root scan succeeded at 2026-11-05 22:26:31 Europe/Istanbul with 7 Low, 0 Medium, 0 High; no new findings. Includes frozen capture service, lease snapshots, native bridge/lifecycle changes, frontend, and own-window preview tests. Owners reported 42 fake capture, 28 HTTP, 49 frontend, 153 rendered UI, 166 Android policy, and 37 Android lease checks passing |
| Final fast-channel test follow-up `snyk_code_scan` | Root scan succeeded at 2026-11-05 22:28:42 Europe/Istanbul: 7 Low, 0 Medium, 0 High; no new findings. Includes the final two `tests/fast.cjs` assertions that preview requests are rejected on the persistent input channel and the next control ping still responds |
| Main preview hotfix `snyk_code_scan` | Root scan succeeded at 2026-11-05 23:08:15 Europe/Istanbul: 7 Low, 0 Medium, 0 High; no new findings. Scope is main `0df805c` plus the frozen `tests/ink-rendered.cjs` retry/error tests. The separate high-refresh worktree is not included |
| Main monotonic capture deadline follow-up `snyk_code_scan` | Root scan succeeded at 2026-11-05 23:21:41 Europe/Istanbul on `ae72437`: 7 Low, 0 Medium, 0 High; no new findings. The native worker now checks elapsed time independently of delayed timer callbacks; the owner reported all 42 capture checks passing |
| `npm audit --prefix tests --json` | Exit 0; 0 vulnerabilities across the reported 6 dependencies |
| `dotnet list companion/TabletDeck.csproj package --vulnerable --include-transitive --format json` | Exit 0; no vulnerable direct or transitive packages reported by NuGet |

The first fixture finding was `javascript/NoHardcodedPasswords/test`. Expanded-scope warnings were `java/TooPermissiveTrustManager`, `javascript/HardcodedNonCryptoSecret/test`, and `javascript/HttpToHttps/test`. All disappeared on rescan without scanner suppression. The Android trust-manager method was unchanged from the preceding source version and already checked the expected pin and validity; the newly reported analyzer warning led to additional platform validation, not removal of the pin or acceptance of arbitrary self-signed peers.

Dependency inputs were rehashed after the expanded-scope changes and remained unchanged from the completed audits: `tests/package-lock.json` SHA-256 `3292BABE92912B8AF34928340384C13E2C92D5F68C334C5048B6AA56F5F8C2C6`; companion project SHA-256 `7888DD9E8FE3D753C05B5E6D1F9A5A19D5BD6F26F836D3EB4E452D5129665870`.

Remaining pre-existing findings:

| Rule | Location | Review context |
| --- | --- | --- |
| `java/JavaScriptEnabled` | `android/src/app/tabletdeck/ChatPane.java:28` | Provider viewer needs JavaScript; isolated from the PC-control bridge |
| `java/JavaScriptEnabled` | `android/src/app/tabletdeck/MainActivity.java:93` | Bundled allowlisted interface needs JavaScript; remote pages are not admitted to the privileged view |
| `javascript/HttpToHttps/test` | `tests/computer-api.cjs:40` | Localhost-only isolated test request |
| `javascript/InsecureTLSConfig/test` | `tests/execution.cjs:11` | Existing isolated self-signed test fixture |
| `javascript/InsecureTLSConfig/test` | `tests/features.cjs:13` | Existing isolated self-signed test fixture |
| `javascript/InsecureTLSConfig/test` | `tests/headless.cjs:35` | Existing isolated self-signed test fixture |
| `javascript/InsecureTLSConfig/test` | `tests/integration.cjs:22` | Existing isolated self-signed test fixture |

Local evidence: `.test-output/snyk-deskwing-mcp-final-2026-11-05.json` (initial integrated findings), `.test-output/snyk-deskwing-mcp-rescan-2026-11-05.json` (post-fix), `.test-output/snyk-deskwing-mcp-confirmed-2026-11-05.json` (self-test follow-up), `.test-output/snyk-deskwing-mcp-expanded-2026-11-05.json` (initial expanded-scope findings), `.test-output/snyk-deskwing-mcp-expanded-rescan-2026-11-05.json` (expanded-scope result), `.test-output/snyk-deskwing-mcp-fullscreen-rescan-2026-11-05.json` (initial fullscreen/window findings), `.test-output/snyk-deskwing-mcp-fullscreen-final-2026-11-05.json` (frozen runtime-scope result), `.test-output/snyk-deskwing-mcp-helptext-final-2026-11-05.json` (latest help-text follow-up), `.test-output/npm-audit-deskwing-2026-11-05.json`, and `.test-output/dotnet-audit-deskwing-2026-11-05.json`. These are ignored artifacts, not release contents. Later security-relevant source or dependency changes require a bounded follow-up scan.

Latest pen-feedback evidence: `.test-output/snyk-deskwing-mcp-pen-feedback-final-2026-11-05.json` (full-root result) and `.test-output/snyk-deskwing-mcp-latency-profiler-2026-11-05.json` (explicit isolated-profiler result). Dependency hashes above were rechecked and remain unchanged.

Latest live-preview evidence: `.test-output/snyk-deskwing-mcp-pen-preview-final-2026-11-05.json`. The new npm entry is a test script only, and the new test projects link existing source without adding package dependencies.

Fast-channel follow-up checkpoint: `.test-output/snyk-deskwing-mcp-preview-tests-final-2026-11-05.json`, after the final fast-channel test additions. Runtime and dependency inputs did not change in that follow-up.

Latest main-app checkpoint: `.test-output/snyk-deskwing-mcp-preview-hotfix-final-2026-11-05.json`, after the preview freshness hotfix and final rendered retry assertions. Dependency hashes remain unchanged.

Subsequent main deadline checkpoint: `.test-output/snyk-deskwing-mcp-preview-deadline-final-2026-11-05.json`, covering `ae72437`. The same dependency hashes remain unchanged.
