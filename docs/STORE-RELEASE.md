# DeskWing store release readiness

Updated on **6 November 2026** from the repository and official platform documentation. This is a release checklist, not evidence that either store has approved the app. The verified `ab55af0` baseline passed 65 Windows suites and 39 iOS simulator tests; the workspace expansion must pass its own checks before a beta is distributed. Record live-console submission, declaration and distribution outcomes separately from this preparation checklist.

The approved public developer name is **Tuğcan Topaloğlu** and the support address is **tugcan@tugcan.dev**. Beta build instructions and the remaining account setup are in [BETA-RELEASE.md](BETA-RELEASE.md). Reviewable public privacy/support pages are prepared under [`release/site`](../release/site/); their expected GitHub Pages URLs are not proof that hosting has been activated.

## Product claims to preserve

DeskWing combines a Windows control deck, app/media shortcuts, shared ink and notes, Windows pen input, and optional independent-display viewing. The companion currently requires Windows. Native Second display sends silent H.264 video at a selected 60 Hz or current-tablet Match target; the separate JPEG compatibility view remains capped at four frames per second. The verified Xiaomi rehearsal observed smooth 60/120 Hz viewing, not a guarantee for every PC, resolution, network or device. An existing compatible virtual-display driver and explicit PC sharing remain prerequisites.

The workspace-expansion beta adds new capabilities in parallel. List notebook/PDF, interactive pen-display and quality features only after they are present and verified in the exact submitted artifact. Keep display capture, Windows input permission and shared-note storage understandable as separate choices; avoid claiming driver-free universal drawing-app compatibility or an Apple Sidecar equivalent.

Describe the drawing scratchpad, handwriting entry and PC controls separately. Android handwriting depends on the OS, stylus and installed keyboard; iPad handwriting depends on supported Apple Pencil/Scribble configuration. Native text-entry support is not a promise of recognition in every language or keyboard. The app must retain ordinary typing.

## Android release gates

| Gate | Requirement and project action |
| --- | --- |
| Target SDK | Current source targets **API 36**, which meets the published new-app/update target requirement effective 31 August 2026. Keep compile/build tools and the behavior-change tests aligned with the manifest. Recheck the Play Console requirement at release freeze. [Google policy](https://support.google.com/googleplay/android-developer/answer/11926878?hl=en) |
| Publishing artifact | Signed **APK, AAB and bundle-derived universal APK** builds exist, with signer and packaged-content parity checks. Use the AAB for Play; retain APKs for the separate direct-install channel. [App bundles](https://developer.android.com/guide/app-bundle) |
| Signing continuity | Play App Signing is used for new apps. Sign uploads with an upload key; Play signs delivered APKs. Decide whether to securely enroll the original app-signing key before first publication so existing sideloaded installs can retain update compatibility. A new incompatible signing certificate cannot silently replace the installed app. Never commit keys/passwords or paste them into chat. [Signing guidance](https://developer.android.com/studio/publish/app-signing) |
| Tablet adaptability | Target 36 ignores orientation/resizability restrictions on displays with smallest width at least 600dp. Test portrait, landscape, split-screen, resized windows, IME insets and saved canvas/editor state; the existing `sensorLandscape` declaration is not a guarantee. [Android 16 changes](https://developer.android.com/about/versions/16/behavior-changes-16) |
| 16 KB devices | Inspect every final APK/AAB dependency for native libraries. Java/Kotlin-only apps already support 16 KB pages, but still need device/emulator testing. The current official page states all Play apps targeting API 35+ must support 16 KB on 64-bit devices, with updates blocked from 1 February 2027 if unsupported. Do not infer compliance from the Windows companion's libraries. [Page-size guidance](https://developer.android.com/guide/practices/page-sizes) |
| Update channel | Keep desktop companion updates separate from mobile store updates. The expansion's PC USB updater checks the selected installed production package's installer/initiator/update-owner provenance and blocks Play-managed, other-store-managed and unknown origins before downloading or installing, including manual USB requests. Known direct installs retain the original-signer path with a fresh source/version/device recheck immediately before installation. Rehearse this in the final artifact; direct distribution remains separate. [Device and Network Abuse policy](https://support.google.com/googleplay/android-developer/answer/16559646) |

The owner explicitly chose to retain the Android package identity `app.tabletdeck.studio` for seamless updates and existing tablet data. Public branding and release files use DeskWing (`DeskWing.apk` and `DeskWing.aab`). The original signing key was recovered and verified during the 1.14 work; its certificate SHA-256 is `B9FACA4DDC4A403A647F6667B065C1CA337F344796D528824BA7CF37C118FD68`. The private key and password stay outside the repository. Enroll and verify the original app-signing identity in Play App Signing before distributing a Play build over the existing direct installation; a matching local upload certificate alone does not establish the delivery certificate. Increment version codes for each new Play artifact and verify signing continuity for direct updates.

### Local network, permissions and background behavior

For the proposed target-36 release, Android's official documentation says local network access remains covered by `INTERNET`; do **not** add/request `ACCESS_LOCAL_NETWORK` for targets 36 or lower. On a future target-37 release, implement the runtime local-network permission or a supported system picker before opening LAN sockets, and handle denial/revocation. This also affects WebView-originated LAN traffic. [Local network permission](https://developer.android.com/privacy-and-security/local-network-permission)

Keep camera permission optional and request it only for QR pairing. Keep drawings and exports in app-private storage or the user-selected document destination; do not add broad storage permission. Suspend previews/control traffic and release held keys when the activity becomes inactive. A persistent foreground service is not needed merely to keep a control panel alive; adding one later requires a real permitted service type and its applicable restrictions. [Foreground-service changes](https://developer.android.com/develop/background-work/services/fgs/changes)

Android 14+ supports stylus handwriting in standard `EditText` and WebView text widgets, provided the stylus and IME support the APIs. Drawing surfaces and nearby text fields need deliberate gesture handling so handwriting does not consume drawing strokes. Verify with the user's real Xiaomi pen, palm contact, Gboard configuration, language and tablet rotation. [Handwriting guide](https://developer.android.com/develop/ui/views/touch-and-input/stylus-input/stylus-input-in-text-fields)

### Play account and testing gates

- Confirm the developer account's verified identity, account type, app record/package reservation and current policy status. Account-holder identity/contact details and organization D-U-N-S requirements belong in the account workflow, not source files. [Required account information](https://support.google.com/googleplay/android-developer/answer/13628312?hl=en)
- Begin with internal testing, then a representative closed test. For personal accounts created after 13 November 2023, the published production-access rule requires at least **12 testers opted in continuously for 14 days**, followed by an application for production access. Do not assume that this rule applies to every existing organization account. [Testing requirements](https://support.google.com/googleplay/android-developer/answer/14151465?hl=en)
- Provide the listing, category, target audience/content rating, screenshots, support contact and public privacy policy. Describe the free/paid Windows companion dependency and all hardware prerequisites accurately. Upload credentials and acceptance of agreements remain with the account holder.

## Privacy inventory before store declarations

Do not preselect “no data collected” merely because there is no DeskWing cloud account. Google defines collection around transmission off the user's device and distinguishes controlled WebViews from open-web browsing. Review the exact shipped behavior, including SDKs, embedded chat and user-initiated transfer to the PC. A Data safety form and privacy policy are required for closed/open/production tracks, including apps declaring no collection; internal-only testing has a stated exemption. [Data safety guidance](https://support.google.com/googleplay/android-developer/answer/10787469?hl=en)

| Data/feature | Known behavior to verify in the release | Disclosure decision |
| --- | --- | --- |
| Pairing PIN, certificate pin and tablet token | Local pairing; token/pin retained on the device; encrypted connection to the selected PC | Explain purpose, local storage, revoke/unpair and deletion; exclude secrets from logs/exports |
| Board labels, action IDs and app/media state | Transferred between paired devices for control/status | Document PC recipient, lifetime and any persisted history |
| OBS/extended-display frames | Optional PC capture sent to the tablet while enabled | Explain explicit start/stop, captured surface and no automatic background capture |
| Sketch strokes, typed/handwritten notes and notebook pages | Content is saved privately on the devices and synchronized with the paired PC; exports are user-selected. The expansion keeps legacy ink intact and adds separate notebook/history storage. | Explain the PC recipient and local recovery/history, deletion and exported-file ownership; exclude document content from diagnostics |
| Imported PDFs | User-selected PDFs are rendered locally using platform APIs or bundled desktop libraries; rendered page backgrounds and annotations may synchronize with the PC. Import does not upload the source PDF to a remote service. | Treat rendered page images as document content; document the private history/retention limits and explicit PDF export destination |
| Handwriting keyboard/IME | Recognition is provided by the user's platform/input method | Do not claim DeskWing guarantees all IME processing stays local; distinguish OS/provider behavior |
| Twitch/Kick viewer | Optional provider pages may contact their own services and use their own storage/cookies | Inventory actual WebView/browser integration and link provider information; never give providers the PC bridge |
| Diagnostics/support | Current diagnostic summaries plus any future user-exported logs | Redact secrets, content and identifiers; retain only what the user intentionally shares |
| App updates | Windows release checks/downloads and separate mobile store updates | Document recipients and channel behavior; do not conflate Windows updates with Play updates |

Publish an accessible privacy policy identifying the developer/support contact, data handling, recipients, retention and deletion. Include it inside the app as well as the store listing. If cloud accounts are added later, revisit account-deletion requirements; current PC pairing is not a cloud user account. [Google User Data policy](https://support.google.com/googleplay/android-developer/answer/10144311?hl=en)

## iOS/iPadOS release gates

The native iOS host is implemented and passed 39 simulator tests at `ab55af0`; the expansion requires a fresh hosted run. See [the iOS plan](IOS-PLAN.md). A simulator pass does not establish signing, physical iPad/Pencil behavior, or TestFlight availability.

| Gate | Required evidence |
| --- | --- |
| Apple build environment | A macOS build using the currently accepted Xcode/SDK. Since 28 April 2026, uploads require Xcode 26+ and iOS/iPadOS 26 SDK+. This is not the deployment-target minimum. [Apple requirements](https://developer.apple.com/news/upcoming-requirements/) |
| Signing/account | Apple Developer Program access, stable bundle ID, app record, team signing and appropriate roles. Keep credentials in Keychain/secure CI secret storage. [Enrollment](https://developer.apple.com/programs/enroll/) |
| Real device | Signed physical-iPad pairing, certificate mismatch rejection, Pencil/Scribble, background release, reconnects, export, resizing and display viewing |
| TestFlight | Internal test first; external testing requires the applicable beta review. Builds expire after 90 days. Apple's current limits are 100 internal and 10,000 external testers. [TestFlight](https://developer.apple.com/help/app-store-connect/test-a-beta-version/testflight-overview/) |
| Privacy/export compliance | Public privacy policy, accurate app-privacy answers, required-reason API/privacy-manifest audit and export-compliance determination for the actual dependencies |
| Review materials | Original opaque App Store/Play artwork is generated by `scripts/Build-StoreAssets.ps1` and the iOS AppIcon catalog is wired into the target. Capture real release screenshots and explain Windows companion setup. Apple's complete-review guidance requests a camera-recorded video of required hardware interaction on a physical Apple device. [Review information](https://developer.apple.com/help/app-review/before-submitting-for-review/complete-review/) |

Apple's privacy definition differs from Google's; on-device-only processing is not collected data under Apple's definition, but embedded web/provider traffic still needs review. Audit native API usage and bundled dependencies before filling labels. [Apple app privacy](https://developer.apple.com/app-store/app-privacy-details/)

For encryption, use Apple's questionnaire and the actual shipped implementation. Apple says cryptography limited to its operating system requires no App Store Connect documentation, but the export declaration still needs to be correct. Do not set `ITSAppUsesNonExemptEncryption` by guessing or treat this checklist as a legal export classification. [Encryption documentation](https://developer.apple.com/help/app-store-connect/reference/app-information/export-compliance-documentation-for-encryption)

## Rehearsal and submission order

1. Freeze the source revision and build both mobile artifacts plus the matching Windows companion; record hashes, signing identity and dependency inventory.
2. Run the full automated regression suite and scan the introduced code. Treat a missing security-scanner/authentication prerequisite as an unresolved gate, not a clean scan.
3. Rehearse on Android and iPad: correct/wrong certificate codes, PIN expiry, revoked pairing, Wi-Fi loss, background/foreground, held keys, rapid cancel, denied camera/local-network access, pen/palm behavior and persisted notes.
4. Check portrait, landscape, multi-window and accessibility at realistic text sizes. Rehearse handwriting using supported and unsupported IMEs/settings.
5. Test actual Windows display creation/sharing separately from preview UI; record frame-rate and input limitations honestly. No driver installation should happen as a surprise on either tablet.
6. Rehearse upgrade continuity and note migration without uninstalling. Never use a fresh signing key or changed package ID to work around an upgrade failure.
7. Complete internal/closed/TestFlight testing and the relevant account-specific production gates. Resolve policy-console findings against the built artifact.
8. Prepare final listing, privacy/support URLs and review instructions. Have the owner review the concrete submission package before publishing it.

The owner has Google Play and Apple Developer accounts, approved the developer/support identity above, and has no local Mac. Hosted macOS can build the iOS host. Remaining non-secret setup includes Apple Team ID and app-record/bundle-ID reservation, Play account/app enrollment status, desired countries/pricing and physical-iPad access. Configure certificate/profile/key material through secure environment secrets or platform tooling, not chat. No passwords, recovery codes or private keys are needed in a conversation.
