# DeskWing iOS host

This directory contains a native iPhone/iPad host for the shared tablet UI. It **builds and passes nine unit tests in the hosted iPad simulator**. It is not yet a signed or released iOS app. Development uses Windows; GitHub's hosted Mac runner provides compilation and simulator validation. Physical iPad, Apple Pencil, pairing and distribution gates below remain required.

## Included

- The same local board, Copilot, silent display/OBS previews, and pen workspace assets as Android, copied by a local build script. No remotely downloaded UI or credentials are bundled.
- Manual pairing over private IPv4 Wi-Fi/LAN. The user enters the PC certificate code and six-digit PIN. `URLSession` verifies the exact host, SHA-256 certificate pin, certificate dates and TLS trust before it can send the PIN. Successful pairing stores the full fingerprint and device token in device-only, when-unlocked Keychain storage.
- A `WKWebView` custom-scheme asset handler. Only bundled manifest assets are served. The native bridge accepts exact allowed paths only from the local main-frame entry page. PC administration routes are not exposed to the tablet.
- HTTPS polling using the existing server API. No persistent fast TLS socket, Android ADB USB tunnel, or QR camera flow is implemented in this first iOS host. The pairing UI states those limits.
- Foreground-only controls, cancellation of pending requests on suspension, best-effort release of held keys, and the server's existing renewable three-second hold lease. Interactive writes that have not started sending within two seconds are cancelled; an already transmitted action may still run and is never automatically repeated by the app.
- Native text entry through `UITextView`, including system Scribble when supported/enabled, plus a keyboard fallback. Handwriting recognition is provided by iPadOS; it is not an app OCR service. A user-selected share/save sheet exports bounded PNG drawings.
- The drawing and pen-tablet full-screen control expands the web view to the app window's edges, hides the status bar and requests Home-indicator auto-hide. It restores normal layout on exit, backgrounding, native tools, re-pairing or renderer reload. iPadOS retains system gestures and decides when the Home indicator hides; this does not force a Split View or Stage Manager window to occupy the entire display.
- Shared ink uses the explicitly allowed `/api/ink` route, with a four-MiB request-body ceiling and revision-aware documents. Its only permitted query form is GET `/api/ink?revision=<64 hexadecimal characters>` for lightweight unchanged polling. Other routes retain the smaller message limits.
- The separate Windows pen-tablet mode uses `/api/pen` leases and bounded sample batches. It forwards Pencil/pen pointer data to the enabled Windows companion input service; it does not install an iPad digitizer driver. The native host cancels its exact pen lease on suspension, never replays batches, and cancels a pen request still waiting to send after half a second. The Windows lease expires after 1.2 seconds without renewal. Physical pressure, tilt, target mapping, application compatibility and latency still require device testing.
- Its live PC drawing preview uses POST `/api/pen/frame` for the active pen lease only. A separate pinned `URLSession` allows one frame at a time with a four-second resource timeout and a three-MiB JSON reply ceiling; preview requests cannot enter the pen-control session. Ending/changing the lease, backgrounding, opening a native tool, navigation or renderer reset cancels the preview, and late responses cannot replace a newer session's image. Frames are JPEG snapshots of the selected PC drawing target, not local tablet ink or a full-motion monitor stream.
- Twitch/Kick public chat opens in `SFSafariViewController`, separate from the privileged web view and without the PC-control bridge. It is a viewer, not an authenticated moderation integration.

The second-display view still uses the Windows companion's existing consented, silent JPEG transport capped at four frames per second. It is not a full-motion monitor protocol. Windows pen injection is a separate, explicitly enabled mode with its own lease and target controls.

## Generate and build on a Mac

Use Xcode 26 or newer with the iOS/iPadOS 26 SDK or newer for App Store upload. The app's deployment minimum is iOS 16; the upload SDK requirement and minimum supported OS are different settings. See [Apple's upcoming requirements](https://developer.apple.com/news/upcoming-requirements/).

Install XcodeGen from its official distribution, then from the repository root:

```sh
python3 ios/sync-assets.py
cd ios
xcodegen generate
xcodebuild -project DeskWing.xcodeproj -scheme DeskWing \
  -configuration Debug -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  CODE_SIGNING_ALLOWED=NO build
```

Run the unit tests using an installed simulator, substituting its actual name from `xcrun simctl list devices available`:

```sh
xcodebuild -project DeskWing.xcodeproj -scheme DeskWing \
  -destination 'platform=iOS Simulator,name=YOUR_INSTALLED_SIMULATOR' \
  CODE_SIGNING_ALLOWED=NO test
```

The XcodeGen source file explicitly places the `www` folder in Copy Bundle Resources, preserving subdirectories. The generated project is ignored. Re-run generation after adding Swift or resource files. The pre-build phase refreshes shared tablet assets, and `sync-assets.py` also runs without Xcode for asset validation.

For a real device, open the generated project, select the user's Apple Developer team, and register the chosen bundle identifier (`app.deskwing.tablet` is a proposed identifier, not a claim that it is available). Keep signing keys and profiles out of this repository. The native name is DeskWing; the Windows/Android installation identities are unchanged by this new iOS target.

## Required validation before TestFlight or App Store

1. Keep the hosted Mac build and `DeskWingTests` passing for the release commit. The recorded simulator pass below verifies compilation and policy tests; it does not replace a signed archive or physical-device validation.
2. Verify that the custom `deskwing://app` origin retains scratchpad/localStorage across app restarts and that all new shared assets load. Confirm VoiceOver, Dynamic Type, orientation, split view, safe areas and touch/Pencil input on an iPad.
3. Rehearse first-run local-network permission denial, allowance in Settings, wrong certificate code, expired certificate, wrong PIN, changed certificate, revoked pairing, reconnects and foreground/background transitions. Confirm the PIN is never sent on a failed trust challenge.
4. Specifically test the self-signed local-IP certificate with `NSAllowsLocalNetworking` on supported iOS versions. No global `NSAllowsArbitraryLoads` exception is configured. Do not add one to make a failed test pass; resolve the pinned local connection policy first.
5. Exercise simultaneous preview/polling and action traffic, queued-write expiry, held-key and pen-lease renewal/release, lost-network ambiguity, long macros, cancellation and restoring the app while a response is pending. Pending actions/sample batches must not replay after reconnect. Verify shared ink conflict recovery and bounded large-document sync separately from latency-sensitive pen input. For the PC drawing preview, verify target alignment, lease/focus changes, one frame in flight, timeout cleanup, stale-image hiding and pen responsiveness during slow image transfer.
6. Test Scribble with actual Apple Pencil, enabled languages, keyboard fallback, long text, cancelling a sheet, pressure/palm handling and drawing save/share. Verify full-screen entry/exit, Home/status-bar behavior, safe-area placement of Exit/Stop controls, Split View, and restoration after backgrounding or native tools. A simulated stylus cannot verify physical palm rejection or system recognition.
7. Provide an App Store icon asset catalog with the required opaque 1024px artwork, accessibility/release screenshots, support/privacy policy URLs, and app review instructions explaining the required Windows companion. The current source target intentionally does not pretend those submission assets are complete.
8. Audit the privacy manifest and App Store privacy answers against final provider behavior and any added SDKs. The supplied manifest is a native-host baseline with no tracking SDKs or required-reason APIs used directly in the current Swift code; it is not an automatic assertion that all product/store disclosures are complete. Do not add an export-compliance exemption flag until the account's accurate declaration is selected.
9. Archive, validate and distribute a signed TestFlight build. Complete an iPad rehearsal before marking iOS support released.

## Security boundaries and primary references

The PC uses a user-verified, self-signed local certificate, so manual trust evaluation is intentional. The app checks the pin and certificate validity, then anchors only that certificate for `Security.framework` evaluation. Credentials do not enter the web view or a remote provider. Public/unvalidated hosts, redirects, admin routes, subframe messages and remote main-page navigation are rejected. See [Apple's manual server trust guidance](https://developer.apple.com/documentation/Foundation/performing-manual-server-trust-authentication).

`NSLocalNetworkUsageDescription` explains the PC connection. No Bonjour discovery or multicast is performed, so those capabilities are not requested. No background audio, VoIP or location mode is used to keep controls running. See [Apple TN3179: local network privacy](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy).

The writing field uses the standard text input system. Recognition availability and language behavior depend on iPadOS settings. See [Apple Scribble support](https://developer.apple.com/documentation/uikit/uiscribbleinteraction).

Project generation follows [XcodeGen's source/resource specification](https://github.com/yonaskolb/XcodeGen/blob/master/Docs/ProjectSpec.md). Store preparation is tracked in [STORE-RELEASE.md](../docs/STORE-RELEASE.md) and [IOS-PLAN.md](../docs/IOS-PLAN.md).

## Recorded validation

- Hosted Mac validation passed on 2026-11-05 for commit `702d40b575133baf1160c285fcb0698599c9ff65`: Xcode 26.6, iPad Pro 13-inch (M5) simulator, iPadOS 26.4.1. The unsigned Debug app compiled and all nine `DeskWingTests` passed with zero failures. [iOS simulator job and logs](https://github.com/tugcantopaloglu/deskwing/actions/runs/42086833842/job/127454109100).
- Tests cover private-address and certificate-pin policy, bridge route/origin boundaries, background release rules, ink request limits, PNG validation, malformed certificates, stale callback isolation after a renderer restart, and strict full-screen requests with background-safe exit. They do not exercise live TLS pairing, visual full-screen behavior or hardware pen input.

- `python ios/sync-assets.py`: shared assets copied with a manifest and path checks.
- Python syntax, JavaScript syntax, XML plist parsing and generated-manifest/source comparison: run locally.
- Signing, a release archive, pinned TLS against the Windows companion, physical Scribble/Pencil and device behavior: **not yet run**.

Security scanner results are recorded separately in the root security review. Do not interpret source scanning as a substitute for the Mac/device gates.
