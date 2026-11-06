# DeskWing 1.15 verification checkpoint

Recorded 2026-11-06 for `codex/workspace-expansion`, based on `ab55af079016542fb4bcfbd0d803bf9d8c520301`. This is the local checkpoint before the development-branch push; final-commit hosted results are tracked separately.

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

The artifacts remain in isolated local output folders; this checkpoint does not record a 1.15 production installation. Existing board content and production settings were not used by these tests.

## Security and remaining verification

The final actual `snyk_code_scan` checkpoint retained **14 Low warnings**: seven baseline findings and seven documented local-CLI/CI path false positives under the reviewed trust model. There were no High, Medium or unexpected actionable new findings, and no scanner suppression was added. All 320 scan inputs stayed unchanged. Working-tree and staged Git blobs for all 201 PDF vendor assets match their manifest; dependency audits report zero known vulnerabilities. See [the security record](SECURITY-EXPANSION-2026-11-06.md) for exact evidence and residual assumptions.

Final-commit hosted Windows regression, iOS simulator tests and unsigned iOS archive validation remain to be observed after push. Physical Xiaomi pen/display rehearsal of 1.15, sustained latency/load/battery measurements, Google Play internal distribution and signed iOS export remain pending. Earlier 1.14 device observations do not establish those results for this candidate.
