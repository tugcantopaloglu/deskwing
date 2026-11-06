# DeskWing 1.15 verification checkpoint

Recorded 2026-11-06 for `codex/workspace-expansion`, based on `ab55af079016542fb4bcfbd0d803bf9d8c520301`. The local checkpoint and subsequent hosted/store outcomes are distinguished below.

## Local regression evidence

All **80 current default suites have successful coverage** against unchanged product source. This is an aggregate, not one uninterrupted green full run:

| Evidence | Result |
| --- | --- |
| Fresh isolated publish and complete 80-suite baseline | 63 passed; one stale asset-list fixture and 16 browser-launch failures retained in the raw report |
| Retry of the 17 failed suites with verified installed headless Chrome, using the same published companion | 14 passed; three stale rendered fixtures remained |
| Owner's focused runs after repairing only those three fixtures | Soundpad 40, workspace 122 and ink 156 checks passed |

The repaired fixtures model the actual studio layout, new display-status reads and the mandatory intended-PC binding for legacy ink. They retain strict request admission and do not relax product guards. Six test files changed after the initial 548-input snapshot; no product or build-helper input changed. All 245 source web files match the published companion, including the notebook/PDF assets; all 108 generated compressed siblings were also checked against their decoded source bytes.

Raw reports remain locally at `.test-output/verification-9fd895/summary.json` and `.test-output/verification-YvLXyh/summary.json`. The aggregate, input inventories and artifact parity are retained under `.test-output/expansion-freeze-20261106T210846/`. Owner results are retained under `.test-output/soundpad-sync/` and `.test-output/rendered-fixture-repairs/`. The default manifest excludes the separate opt-in installer group.

## Candidate artifacts

Windows metadata is **DeskWing 1.15.0**. Android retains package `app.tabletdeck.studio`, version **1.15.0 / code 18**, and its original signing certificate. APK/AAB/universal-APK validation passed with 320 unchanged packaging inputs, all 235 shared allowlisted assets, 201 pinned PDF vendor files, 238 matching web files and complete packaged-asset/DEX parity. Bundle validation passed.

| Artifact | SHA-256 |
| --- | --- |
| Isolated `DeskWing-Companion.exe` | `81e19483b40f73b7448165aa7d0ab1cf3892618e185201741705d9b2d8ef369b` |
| `DeskWing.apk` | `6809648588b6b1c16cb6281a9b39af8eef3b8dd2e02afa7825c11f3a408e0541` |
| `DeskWing.aab` | `1e92eacad3f18dc78785c049edb7a1586651d5327991a985eb9f35df77055124` |
| `DeskWing-universal.apk` | `968c5f5b465a2f5f369397e86e33f96f0ae8d57d3a7873471c204f80482d5812` |

These build and regression checks used isolated output folders and fixture data. Existing board content and production settings were not used by the tests; production deployment and physical-device results are tracked separately.

## Hosted and Google Play follow-up

Commit [`79bdbd0`](https://github.com/tugcantopaloglu/deskwing/commit/79bdbd0173a0163f59ee7ffcb7324168a928bebe) passed an uninterrupted **80-suite Windows regression** in [hosted verification](https://github.com/tugcantopaloglu/deskwing/actions/runs/42271174594), plus [unsigned Android APK/AAB validation](https://github.com/tugcantopaloglu/deskwing/actions/runs/42271174903) and the [generic unsigned iOS archive](https://github.com/tugcantopaloglu/deskwing/actions/runs/42271174724).

The same hosted iOS simulator job executed 70 tests and found three test-harness failures. Two WKWebView tests accidentally selected the completion-handler overload returning `Void`; they now explicitly await the real callback with a bounded watchdog, retaining all secure-origin, IndexedDB and revision assertions. The raster fixture now checks Core Graphics' standardized negative-size geometry while retaining rejection of zero, oversized and infinite dimensions. Only two XCTest files changed; the corrected hosted simulator result remains pending for the follow-up commit.

The owner completed Google Play App Signing enrollment with the original app-signing certificate verified in Console, then published **DeskWing 1.15.0 / code 18 to Internal testing**, available to invited testers. The [internal opt-in link](https://play.google.com/apps/internaltest/4701292919537220543) requires an eligible tester account. This records an internal release; production review and rollout are separate.

The matching Windows companion was subsequently deployed in the normal user context with the exact executable hash listed above. Its production check preserved all six pages, 21 saved buttons, Memes routing to both outputs and the hashes of four protected files. A private backup and the prior 1.14 runtime were retained. The owned display was restored without changing physical-monitor topology; the new display-input permission remains off. This verifies the PC deployment and data continuity, not installation or physical stylus rehearsal of the Play update on the tablet.

## Security and remaining verification

The final actual `snyk_code_scan` checkpoint 7 includes the two Swift test corrections and retained **14 Low warnings**: seven baseline findings and seven documented local-CLI/CI path false positives under the reviewed trust model. There were no High, Medium or unexpected actionable new findings, and no scanner suppression was added. All 320 scan inputs stayed unchanged. Working-tree and staged Git blobs for all 201 PDF vendor assets match their manifest; dependency audits report zero known vulnerabilities. See [the security record](SECURITY-EXPANSION-2026-11-06.md) for exact evidence and residual assumptions.

The corrected follow-up commit's hosted results remain to be observed after push. Physical Xiaomi pen/display rehearsal of 1.15, sustained latency/load/battery measurements, production store rollout and signed iOS export remain pending. Earlier 1.14 device observations do not establish those results for this candidate.
