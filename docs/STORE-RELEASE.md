# DeskWing store release readiness

Prepared on **5 November 2026** from the repository and official platform documentation. This is a release checklist, not evidence that either store has approved the app. Android and iOS work is underway in parallel; check the finished artifacts against every gate below. Store submissions, account agreements and payments have not been performed.

## Product claims to preserve

DeskWing combines a Windows control deck, app/media shortcuts and tablet pen notes. The companion currently requires Windows. Optional independent-display viewing requires a compatible driver already installed on that PC, explicit PC consent and a genuinely extended desktop. The existing transport is a silent JPEG preview capped at four frames per second, with no touch forwarding. Do not market that implementation as full-motion video, an Apple Sidecar equivalent, or a remote drawing digitizer.

Describe the drawing scratchpad, handwriting entry and PC controls separately. Android handwriting depends on the OS, stylus and installed keyboard; iPad handwriting depends on supported Apple Pencil/Scribble configuration. Native text-entry support is not a promise of recognition in every language or keyboard. The app must retain ordinary typing.

## Android release gates

| Gate | Requirement and project action |
| --- | --- |
| Target SDK | New apps and updates submitted to Google Play must target **API 36 or higher** from 31 August 2026. The inspected 1.13 baseline targets 35. Upgrade compile/build tools and target, then test the behavior changes; raising a manifest integer alone is insufficient. The published extension date was 1 November 2026, already past on this checklist date. [Google policy](https://support.google.com/googleplay/android-developer/answer/11926878?hl=en) |
| Publishing artifact | New Play apps require an **Android App Bundle**. The existing custom script builds an APK for direct installation; retain that path and add a validated, signed AAB path. [App bundles](https://developer.android.com/guide/app-bundle) |
| Signing continuity | Play App Signing is used for new apps. Sign uploads with an upload key; Play signs delivered APKs. Decide whether to securely enroll the original app-signing key before first publication so existing sideloaded installs can retain update compatibility. A new incompatible signing certificate cannot silently replace the installed app. Never commit keys/passwords or paste them into chat. [Signing guidance](https://developer.android.com/studio/publish/app-signing) |
| Tablet adaptability | Target 36 ignores orientation/resizability restrictions on displays with smallest width at least 600dp. Test portrait, landscape, split-screen, resized windows, IME insets and saved canvas/editor state; the existing `sensorLandscape` declaration is not a guarantee. [Android 16 changes](https://developer.android.com/about/versions/16/behavior-changes-16) |
| 16 KB devices | Inspect every final APK/AAB dependency for native libraries. Java/Kotlin-only apps already support 16 KB pages, but still need device/emulator testing. The current official page states all Play apps targeting API 35+ must support 16 KB on 64-bit devices, with updates blocked from 1 February 2027 if unsupported. Do not infer compliance from the Windows companion's libraries. [Page-size guidance](https://developer.android.com/guide/practices/page-sizes) |
| Update channel | A Play-distributed app must not replace/update itself outside Play. Keep desktop companion updates separate. The PC's direct APK/USB workflow must not automatically overwrite a Play installation; identify the installed channel and direct store users to Play. Direct distribution remains a separate release path. [Device and Network Abuse policy](https://support.google.com/googleplay/android-developer/answer/16559646) |

The Android package identity remains `app.tabletdeck.studio`; changing its visible label to DeskWing does not require changing the package. Retain monotonically increasing version codes and verify the signing certificate against the installed build before any update. The original signing key was not established by this document's inspection; the release owner must identify it securely.

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
| Sketch strokes and typed/handwritten notes | App-local persistence and explicit file export in the intended design | Verify no unintended backup, sync, telemetry or support-log inclusion |
| Handwriting keyboard/IME | Recognition is provided by the user's platform/input method | Do not claim DeskWing guarantees all IME processing stays local; distinguish OS/provider behavior |
| Twitch/Kick viewer | Optional provider pages may contact their own services and use their own storage/cookies | Inventory actual WebView/browser integration and link provider information; never give providers the PC bridge |
| Diagnostics/support | Current diagnostic summaries plus any future user-exported logs | Redact secrets, content and identifiers; retain only what the user intentionally shares |
| App updates | Windows release checks/downloads and separate mobile store updates | Document recipients and channel behavior; do not conflate Windows updates with Play updates |

Publish an accessible privacy policy identifying the developer/support contact, data handling, recipients, retention and deletion. Include it inside the app as well as the store listing. If cloud accounts are added later, revisit account-deletion requirements; current PC pairing is not a cloud user account. [Google User Data policy](https://support.google.com/googleplay/android-developer/answer/10144311?hl=en)

## iOS/iPadOS release gates

The iOS host is separate implementation work; see [the iOS plan](IOS-PLAN.md). Shared HTML/JavaScript alone is not an iOS build.

| Gate | Required evidence |
| --- | --- |
| Apple build environment | A macOS build using the currently accepted Xcode/SDK. Since 28 April 2026, uploads require Xcode 26+ and iOS/iPadOS 26 SDK+. This is not the deployment-target minimum. [Apple requirements](https://developer.apple.com/news/upcoming-requirements/) |
| Signing/account | Apple Developer Program access, stable bundle ID, app record, team signing and appropriate roles. Keep credentials in Keychain/secure CI secret storage. [Enrollment](https://developer.apple.com/programs/enroll/) |
| Real device | Signed physical-iPad pairing, certificate mismatch rejection, Pencil/Scribble, background release, reconnects, export, resizing and display viewing |
| TestFlight | Internal test first; external testing requires the applicable beta review. Builds expire after 90 days. Apple's current limits are 100 internal and 10,000 external testers. [TestFlight](https://developer.apple.com/help/app-store-connect/test-a-beta-version/testflight-overview/) |
| Privacy/export compliance | Public privacy policy, accurate app-privacy answers, required-reason API/privacy-manifest audit and export-compliance determination for the actual dependencies |
| Review materials | Explain Windows companion setup and provide a physical-device demonstration of the required hardware interaction. Apple's complete-review guidance requests a camera-recorded video for hardware-dependent apps. [Review information](https://developer.apple.com/help/app-review/before-submitting-for-review/complete-review/) |

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

Account details still needed from the owner are non-secret: which account types already exist, who holds the signing key/team role, desired countries/pricing, support email/privacy-policy host, and available Mac/iPad test access. Do not request passwords, recovery codes or private keys in chat.
