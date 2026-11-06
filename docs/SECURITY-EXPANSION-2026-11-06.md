# DeskWing workspace expansion security review — 2026-11-06

Status: final actual frozen-source follow-up scan 7 reports **14 Low findings**: seven preceding findings and seven independently reviewed local-CLI/CI false positives. There are **zero High/Medium findings and no unexpected or actionable new findings**. All **320** captured source/configuration inputs were unchanged during the scan. Manual security review is complete with no unresolved code findings. This review does not claim hosted signing, store submission or physical device release validation.

## Scope

The expansion adds tablet workspace/audio status, a packaged-host startup guard, lease-bound pen/touch input for the owned native-video display, notebooks and PDF workflows, video quality/encoding choices, and beta-release preparation. The existing board, pairing, signing identity, and legacy scratchpad require preservation.

Security review uses code and isolated tests. Reviewers do not operate the user's apps, inject real input, play audio, change the live board, or submit store builds.

## Scanner preparation and baseline

The cached official Snyk npm wrapper hosts Snyk MCP Server **1.1308.3** with `mcp -t stdio`. The server advertises the actual `snyk_code_scan` tool. No authentication, trust, suppression, or scanner-exclusion setting was changed. The existing `.dcignore` excludes generated output and vendored third-party files; those exclusions are not first-party coverage claims.

The scan targets the complete checkout for Snyk-supported source languages. PowerShell/CMD and workflow ownership/secret/path boundaries also receive manual review. Source inventories include build/configuration inputs; their counts do not assert scanner language coverage or a per-file analysis count.

The preceding source checkpoint retained seven Low findings: the intended JavaScript-enabled Android WebViews and existing isolated loopback/TLS test fixtures. Results are compared by rule, checkout-relative path, severity and occurrence count rather than line numbers.

## Dependency review

An isolated metadata-only npm audit of exact `pdfjs-dist` **6.5.438** and `pdf-lib` **1.17.1** resolved a lockfile with install scripts disabled and reported **zero known vulnerabilities**. A separate audit of the versions embedded in pdf-lib's published bundle (`@pdf-lib/upng` 1.0.1, `@pdf-lib/standard-fonts` 1.0.0, pako 1.0.10/1.0.11, and tslib 1.11.1) also reported zero known vulnerabilities. This metadata tree includes upstream dependencies; it does not imply that every optional upstream package is shipped.

Independent final verification matched all **201** vendor assets (**4,868,477 bytes**) and SHA-256 hashes against `scripts/notebook-pdf-vendor.json`, including the embedded-library license notices added during packaging. The same **201 staged Git blobs** also matched, verifying that the new `-text` attributes preserve upstream bytes across commits. Both official npm tarball URLs and SHA-512 integrities matched registry-resolved lock metadata. Apache-2.0/MIT and the bundled font/CMap notices are retained. Vendor files are dependency-reviewed, not claimed as first-party SAST coverage.

Local evidence: `.test-output/pdf-dependency-audit/package-lock.json`, `.test-output/pdf-dependency-audit/npm-audit.json`, and `.test-output/pdf-bundled-audit/npm-audit.json`. No application lockfile was replaced by this audit.

## Manual findings repaired during implementation

- High-rate Android stylus events no longer postpone the first batch indefinitely; scheduling retains the earliest flush deadline.
- Native input uses bounded shared request/cleanup admission and an independent total HTTP deadline. A blocked write cannot accumulate a new unbounded executor for every viewer replacement.
- The native viewing layer consumes touches in View mode and unused margins rather than dispatching them to hidden controls below it.
- Hardware-transform discovery releases every returned activation reference and uses one bounded probe budget with cancellation checks before fallback.
- Video/input authority and the HTTP observer retire before native producer disposal. The worker remains reserved until both native cleanup and any noncooperating raw write settle; cleanup exceptions cannot release it early.
- Notebook storage uses actual capacity accounting, explicit archived-data/history recovery, and atomic legacy-ink/history ordering. Current page data is not silently deleted to make room.
- Thumbnail image decoding is awaited within the bounded job queue; history identities use their actual 64-hex revision format.
- Native PDF cancellation closes resources that finish opening after retirement. Both platforms apply the same exact four-million-pixel bound.
- Flattened PDF export flushes each embedded image before proceeding so decoded image buffers are not retained for the entire document.
- PDF executable modules use fixed local imports. Generated downloads require a bounded PDF Blob, header/EOF checks, a fresh exact-media-type Blob and a same-origin UUID object URL; URLs are released after use.
- Notebook request envelopes retain the original PC pairing context and validate it before HTTP body dispatch and on completion. Native handles and JavaScript import intent are retired on PC changes.
- The original scratchpad now retains that same intended-PC binding through its GET/snapshot and later POST. A different or unknown workspace cannot silently receive an old nonempty local copy merely because its remote scratchpad is empty.
- Notebook-only IndexedDB writes use transactions and local revision checks. Unavailable durable storage is read-only; divergent or damaged branches remain independently reviewable/exportable. Saving a reviewed copy creates a new page, and dynamic storage loss cannot discard an unsaved writable draft during page switching.
- Import/resume captures its original task, PC context, epoch, page generation and cancellation signal before the first await. Retired completion cannot clear a newer task or cancellation controller.
- Beta IPA staging validates one owned, bounded ordinary file and uses the same no-follow descriptor for validation, copying and hashing. CLI paths cannot select arbitrary file sinks; Python no longer writes GitHub file-command paths.
- Windows branding preserves existing data/install markers, exact process and startup-command ownership, foreign/non-string Run values and absent opt-in. Strict current/legacy update assets, bounded installer paths and rollback/restart behavior remain intact. Explicit data paths are parsed before startup identity checks and never silently fall back when malformed.

These are source review/remediation checkpoints. Their owners run isolated regressions; reviewers do not claim to have injected real input, played audio, copied private documents, or measured physical performance. Final manual readback and the frozen-source rescan cleared the scanner-directed changes, Windows upgrade/startup changes and network-timeout/input-authority ordering. Build/device release validation remains with the integration owner.

## Reviewed security boundaries

- Startup identity fails before opening or writing an implicit redirected production data directory; it does not migrate or replace the user's board.
- Paired tablet context excludes admin credentials and private filesystem paths. Audio tests require an explicit user gesture and current action/profile/page/route validation.
- Display input retains separate PC opt-in, exact video owner/lease/source identity, fresh geometry, short sequence-bound leases, bounded queues, and release on invalidation. Native overlays consume touches rather than expose hidden board controls.
- Native input network operations use independent total deadlines and bounded cancellation/admission. Blocked writes cannot accumulate workers or retain input authority after retirement.
- Notebook storage preserves legacy scratchpad data, uses opaque IDs and revision checks, bounds pages/history/import bytes/pixels and total storage, and provides explicit capacity recovery.
- PDF rendering is data-only, with local renderer/native OS import, no viewer/scripting bundle, XFA disabled, bounded native handles and no arbitrary remote/local-path input. Flattened export does not preserve original interactive objects.
- Adaptive encoding retains fixed authenticated geometry and one bounded worker; asynchronous work settles before resource ownership is reused.
- Beta signing source uses temporary private secret storage, exact identity checks, intended-deliverable staging and cleanup, with no automatic store-upload action.

## Scanner checkpoints

Request 3 used the official MCP `snyk_code_scan` on the complete expansion checkout. Its before/after inventory contained **315** first-party/configuration inputs with **zero drift**. The exact JSON-RPC request, raw result and hash inventories are retained under `.test-output/snyk-expansion/`.

The scan retained the seven preceding Low findings and added twelve: one High and one Medium DOM-XSS dataflow finding in PDF asset loading/download handling, three Low beta-helper path findings, one Low install-source field-normalization finding, and six Low isolated-test-server findings. These triggered the repairs and data-source proof review recorded below. No scanner suppression or exclusion was added.

Request 4 scanned the complete tree after remediation and branding. All **319** captured first-party/configuration inputs remained unchanged during the scan. Both product DOM-XSS findings and the install-source field warning were gone. Its raw result contained **18 Low warnings**: the seven preceding warnings, five command-line-selected data-directory flows, two runner-temporary-directory flows and four isolated-fixture warnings. The local CLI/CI path flows received the explicit trust-source classification below; subsequent fixture changes were test-only.

Request 5 scanned after the three authorized test-only changes (`tests/monitor.cjs`, `tests/notebook-ui.cjs`, `tests/notebook-pdf.cjs`). Independent hash comparison confirmed every product/build input was identical to request 4. It reports **14 Low findings**: seven retained preceding findings and the seven reviewed local-CLI/CI path warnings below. Every residual warning's exact fingerprint matches its reviewed request-4 trace. There are no unexpected findings; the remaining fixture warnings are gone. All **319** captured inputs stayed unchanged across the scan.

Checkpoint 5 was verified at **2026-11-06 18:17:24 UTC**, on the working tree based on `ab55af079016542fb4bcfbd0d803bf9d8c520301`. The deterministic sorted source/hash inventory SHA-256 is `1964b353324fd6bac681f6d30fd28a2ef4673b886661f6a3ee14403326c7a44c`. Exact request/result, baseline comparison and before/after hashes are in `.test-output/snyk-expansion/{request-5,result-5,comparison-5,source-before-5,source-after-5}.json`.

Request 6 scanned the final three rendered-fixture fixes and `.gitattributes`. Independent comparison confirmed those are the only changes after checkpoint 5; product/build source stayed identical. The actual result remains **14 Low findings**, with the same seven baseline findings and exact seven reviewed residual fingerprints, no unexpected findings and no High/Medium findings. All **320** captured inputs stayed unchanged during this final scan.

Final checkpoint 6 was verified at **2026-11-06 18:25:03 UTC**, based on the same working-tree HEAD. Its sorted source/hash inventory SHA-256 is `f64c2dbfc3af617ab7002dfb6c99656887ffa21ef8b5dc4b88543f4ba066a53e`. Evidence is `.test-output/snyk-expansion/{request-6,result-6,comparison-6,source-before-6,source-after-6}.json`. The official scanner exited cleanly after verification.

Follow-up request 7 covers two XCTest-only corrections after hosted execution on `79bdbd0173a0163f59ee7ffcb7324168a928bebe`: `NotebookStorageWebTests.swift` explicitly awaits the real WK JavaScript completion with a once-only checked continuation and ten-second watchdog; its real secure-origin, IndexedDB and CAS assertions remain. `NotebookDocumentPolicyTests.swift` correctly treats standardized negative CGRect sizes while retaining zero/oversized/nonfinite rejection. Independent hash comparison confirmed these are the only source changes after checkpoint 6. No product, Android or web source changed.

The actual full-tree follow-up, using a tool that explicitly advertises Swift support, retains the **exact same fourteen Low rule/path/severity/fingerprint entries** as checkpoint 6, with no unexpected or actionable new findings. All **320** inputs remained unchanged. Verified at **2026-11-06 18:44:36 UTC**, its sorted source/hash inventory SHA-256 is `748e0eb67ada4ecc5ff2c1a7d90955741b4e870fd3ddd6cc526758040d86aee6`. Evidence is `.test-output/snyk-swift-followup/{request-7,result-7,comparison-7,source-before-7,source-after-7}.json`; the official scanner exited cleanly. Hosted XCTest validation of the corrected fixtures remains a separate integration gate.

## Residual path-warning classification

The five Low `csharp/PT` flows at `Program.cs` lines 41, 47, 56, 80 and 130 start with **local process arguments**, before app/Kestrel creation. `--data-dir` deliberately selects a directory the launching user can access. The parser rejects missing, blank, duplicate, next-option and invalid path values; it resolves syntax and never substitutes the default for a malformed explicit value. HTTP/notebook/ink routes cannot assign this get-only root. The companion does not request elevation, the selected directory receives current-user/SYSTEM permissions or startup fails, and automatic update refuses custom-directory instances rather than constructing a remotely supplied restart path. These warnings describe the intended local directory selector, not a lower-trust caller escaping a filesystem boundary. Independent source review classifies them as false positives for this launch model.

The two Low `python/PT` flows at `beta-ios.py` lines 62 and 240 start with GitHub's platform-provided `RUNNER_TEMP`, not a dispatch input. The checked-in workflow does not override it; the helper combines only literal relative paths, compares raw CLI spellings, rejects links/foreign ownership, and opens the exact expected IPA once. It never extracts ZIP entries. The same bounded descriptor feeds validation, copy and hashing, with exclusive private output and a final source-metadata check. Signing requires manual dispatch from `main` and the `ios-beta` environment. GitHub documents its default runner variables and filesystem paths, and environment protections gate access to configured secrets. [GitHub variables reference](https://docs.github.com/en/actions/reference/workflows-and-actions/variables), [GitHub environment protections](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments).

Independent review classifies these two warnings as false positives **within the reviewed trusted-runner/reviewed-release-code model**. Environment protections must be configured before supplying signing credentials. The classification does not cover compromised runners, malicious release-code authors, privileged arbitrary local invocations or future untrusted path inputs. All seven warnings remain in raw results; no ignore/suppression/exclusion was added and a zero-finding scan is not claimed.

## Final checkpoint

Product remediation, branding manual readback, final fixture source and checkpoint-7 security triage are complete. There are no pending unreviewed code findings. The final 201-asset PDF working-tree/staged-blob provenance verification passed; dependency audits report zero known vulnerabilities. Raw residual warnings remain visible; no zero-finding claim or scanner suppression is made. The integration owner's build/device results and configured signing/store release gates remain separate.

A source-only one-off local publishing helper outside the public source inventory received a separate actual `snyk_code_scan` before execution. Its bounded file-read correction was reviewed; the raw result contains one Low `java/PT` warning for its intended local-argv-selected password file, classified as the same-user file-selector false positive. The helper source hash stayed unchanged during that scan. No keystore, password or encrypted export was present in its scan folder, and security reviewers did not execute it or access those files. Separate evidence is retained under `.test-output/snyk-play-key-launcher/`; this result is not included in the repository's fourteen-warning count.
