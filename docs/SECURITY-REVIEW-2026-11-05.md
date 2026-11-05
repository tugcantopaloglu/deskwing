# DeskWing security review — 2026-11-05

Status: integrated scan, remediation, rescan, and dependency audits complete. No newly introduced Snyk finding remains. Seven pre-existing Low findings remain documented below.

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

The iOS source uses a bundled-page/main-frame bridge gate, exact route allowlists, a private numeric PC address, immutable SHA-256 certificate pins verified before credentials are sent, bounded responses, no redirects, and a device-only Keychain item. A queued-write concern was corrected with a two-second deadline for controls whose request body has not started; release/cancel requests are exempt, long macro responses are not cut off merely for taking time, and ambiguous on-wire races are never retried. This is source review only: macOS compilation, Apple trust-stack behavior, URLSession timing, and physical-device validation remain separate gates.

The Android packaging scripts confine proto-resource extraction to their new module directory, verify pinned tool hashes before execution, keep development signing/package identity separate, pass signing passwords by file, and refuse missing release keys. These PowerShell scripts were manually reviewed; PowerShell is not among the supported languages listed by the Snyk Code tool. The installer shortcut migration compares full canonical targets and preserves unrelated shortcuts.

## Final verification

The root-directory `snyk_code_scan` call included the integrated C#, Android Java, JavaScript, Python and iOS Swift source under the normal `.dcignore` exclusions. The MCP response does not report a per-language file-count breakdown. The scan does not compile or execute the iOS host.

| Check | Actual result |
| --- | --- |
| Initial integrated `snyk_code_scan` | Success; 8 Low, 0 Medium, 0 High |
| Remediation | Replaced a newly introduced literal test password in `tests/reliability-api.cjs` with `crypto.randomBytes(32).toString('hex')`; retained a private exact-value persistence assertion |
| Relevant regression | Author rebuilt cleanly and ran 16 reliability API checks successfully |
| Post-fix `snyk_code_scan` | Success; 7 Low, 0 Medium, 0 High; no new findings compared with baseline |
| Follow-up after default self-test stopped performing WAV playback | Root-directory `snyk_code_scan` again succeeded with the same 7 Low / 0 Medium / 0 High findings; no new findings |
| `npm audit --prefix tests --json` | Exit 0; 0 vulnerabilities across the reported 6 dependencies |
| `dotnet list companion/TabletDeck.csproj package --vulnerable --include-transitive --format json` | Exit 0; no vulnerable direct or transitive packages reported by NuGet |

The new fixture finding was `javascript/NoHardcodedPasswords/test`. It disappeared on rescan without scanner suppression. Dependency inputs were unchanged after the audits: `tests/package-lock.json` SHA-256 `3292BABE92912B8AF34928340384C13E2C92D5F68C334C5048B6AA56F5F8C2C6`; companion project SHA-256 `7888DD9E8FE3D753C05B5E6D1F9A5A19D5BD6F26F836D3EB4E452D5129665870`.

Remaining pre-existing findings:

| Rule | Location | Review context |
| --- | --- | --- |
| `java/JavaScriptEnabled` | `android/src/app/tabletdeck/ChatPane.java:28` | Provider viewer needs JavaScript; isolated from the PC-control bridge |
| `java/JavaScriptEnabled` | `android/src/app/tabletdeck/MainActivity.java:80` | Bundled allowlisted interface needs JavaScript; remote pages are not admitted to the privileged view |
| `javascript/HttpToHttps/test` | `tests/computer-api.cjs:40` | Localhost-only isolated test request |
| `javascript/InsecureTLSConfig/test` | `tests/execution.cjs:11` | Existing isolated self-signed test fixture |
| `javascript/InsecureTLSConfig/test` | `tests/features.cjs:13` | Existing isolated self-signed test fixture |
| `javascript/InsecureTLSConfig/test` | `tests/headless.cjs:35` | Existing isolated self-signed test fixture |
| `javascript/InsecureTLSConfig/test` | `tests/integration.cjs:22` | Existing isolated self-signed test fixture |

Local evidence: `.test-output/snyk-deskwing-mcp-final-2026-11-05.json` (initial integrated findings), `.test-output/snyk-deskwing-mcp-rescan-2026-11-05.json` (post-fix), `.test-output/snyk-deskwing-mcp-confirmed-2026-11-05.json` (latest root-directory follow-up), `.test-output/npm-audit-deskwing-2026-11-05.json`, and `.test-output/dotnet-audit-deskwing-2026-11-05.json`. These are ignored artifacts, not release contents. Later security-relevant source or dependency changes require a bounded follow-up scan.
