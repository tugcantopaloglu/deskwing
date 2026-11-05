# DeskWing iOS/iPadOS implementation and release plan

Prepared **5 November 2026**. This describes the secure native host required to reuse DeskWing's tablet interface. Source preparation is not proof of a successful Apple build, signing, device compatibility or App Store approval. Implementation in `ios/`, if present, must be checked against this plan and the [store release gates](STORE-RELEASE.md).

## Scope and honest capability boundary

The first iPad release should provide local Wi-Fi pairing, the existing board/media/app controls, pen notes and drawing, native handwriting entry, and optional preview/display viewing. The Windows companion remains required for PC features. The scratchpad should remain useful offline.

The Android ADB reverse-tunnel/USB workflow does not transfer to iOS. Do not show “USB connection” as available without a separately implemented and tested transport. Optional extended Windows display creation remains a PC-side driver/topology operation; the iPad host receives its frames. The current JPEG transport is not full-motion video, audio streaming, or pen/mouse input forwarding to the desktop.

## Recommended architecture

The following is engineering design based on the existing product and Apple APIs, not an Apple-prescribed architecture.

| Component | Responsibility and boundary |
| --- | --- |
| Native app shell | Swift/SwiftUI or UIKit app with WKWebView for the bundled tablet UI; safe-area and resizing support; native pairing/permissions/error presentation |
| Asset loader | Only versioned, bundled assets; allow file reads only within that asset directory or a tightly validated custom asset scheme. Never fetch privileged app code from a mutable website. [WKWebView file loading](https://developer.apple.com/documentation/webkit/wkwebview/loadfileurl(_:allowingreadaccessto:)) |
| Bridge adapter | Preserve the current `DeckNative.dispatch`/`nativeResult` contract where feasible, but validate every message's frame, origin, route, method, payload type and byte size in Swift. Only the bundled main frame can issue commands. [WKFrameInfo](https://developer.apple.com/documentation/webkit/wkframeinfo) |
| Companion client | A native HTTPS client with certificate-code verification/pinning, bounded requests and no automatic command replay; add the persistent TLS/state channel only with equivalent validation and lifecycle tests |
| Credential store | Keychain for device credential and certificate pin; device-only accessibility appropriate to foreground use; ordinary preferences for non-secret settings |
| Pen notes | Shared scratchpad for initial behavior; native text view for Scribble, and optionally PencilKit for a later native drawing experience |
| External provider viewing | An entirely separate bridge-free web view or system browser. No remote navigation in the privileged web view and no provider access to companion credentials |
| Lifecycle coordinator | Cancel queued writes and previews, release owned holds, invalidate operation/context tokens and disconnect when inactive; recover current state on return without replaying actions |

### Pairing and request security

1. Enter a PC LAN address or scan the existing offline QR payload. Validate host, port and payload bounds; display the intended PC/certificate code before pairing.
2. Establish the TLS peer identity and compare the displayed certificate code **before sending the pairing PIN**. The current protocol uses a short out-of-band certificate code and stores the full fingerprint after successful pairing; carry those semantics across without a trust-all bootstrap.
3. Store the full peer pin and tablet credential in Keychain. A changed certificate or revoked credential requires an explicit re-pair flow; it never triggers silent trust or retry with weaker security.
4. Restrict the native client to the paired PC and exact allowlisted routes. Never expose an arbitrary URL fetcher or PC-admin routes to JavaScript. Apply body/frame limits, method checking and finite request expiry.
5. Keep credentials out of WebView localStorage, exported notes, screenshots, diagnostic bundles and logs. Redact peer identity where a support report does not need it.
6. Use current state/context tokens for actions; reject obsolete contexts. A timeout means unknown outcome, not permission to replay a command. Retain cancel semantics and the server's short hold lease.

`URLSession` provides custom trust evaluation, but its delegate cannot relax ATS requirements for ATS-protected domains. The actual self-signed local-IP connection must be validated on the supported iOS versions. Scope any local-network ATS configuration to the intended use; do not add global `NSAllowsArbitraryLoads`, trust every certificate, or weaken provider-page HTTPS. If a native transport implementation changes, review its trust callback independently. [Manual trust evaluation](https://developer.apple.com/documentation/Foundation/performing-manual-server-trust-authentication), [local networking configuration](https://developer.apple.com/documentation/bundleresources/information-property-list/nsapptransportsecurity/nsallowslocalnetworking)

## Platform permissions and pen behavior

Include `NSLocalNetworkUsageDescription` for connecting to the user's PC, including direct unicast. Make the first connection attempt while foregrounded and give a useful denial/retry path. Add `NSBonjourServices` only if the host actually browses/registers the declared Bonjour service types; multicast/broadcast functionality needs its own entitlement assessment. Manual-IP/QR pairing avoids making discovery a prerequisite. [Local-network privacy](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)

Camera access, if implemented for QR scanning, is optional and requested in context; manual pairing must still work. Use the platform document/share interface for exports instead of broad file access. Do not request microphone, Photos, tracking, background-audio or location permissions for features that do not need them.

Native `UITextView`/`UITextField` surfaces offer the straightforward Scribble path. Custom virtual text fields require additional interaction support. Separate handwriting text-entry bounds from the canvas so the same Pencil stroke is not treated as both drawing and text input. Standard typing stays available when Scribble is disabled or unsupported. [Scribble interaction](https://developer.apple.com/documentation/uikit/uiscribbleinteraction), [indirect Scribble](https://developer.apple.com/documentation/uikit/uiindirectscribbleinteraction-2dap8)

If a native drawing layer is added, `PKCanvasView` provides drawing data and a drawing policy for Pencil versus finger input. Respect the user's preference and make pen-only drawing visible; do not claim software alone can reproduce every device's palm-rejection behavior. [PencilKit canvas](https://developer.apple.com/documentation/pencilkit/pkcanvasview)

iOS ordinarily suspends inactive apps. Use foreground reconnect/reconciliation; a control socket does not justify declaring audio, location or VoIP background modes. [Background execution](https://developer.apple.com/documentation/Xcode/configuring-background-execution-modes)

## Build and verification milestones

| Milestone | Exit evidence |
| --- | --- |
| Source host | Xcode project/manifest, bundled asset synchronization, explicit bridge route manifest, tests for message validation and pairing state machine |
| Simulator | Clean macOS simulator build, launch/navigation, denied-network explanation, typed notes/export, compact/large layout and lifecycle tests |
| Security integration | Real companion pairing; wrong pin/certificate/PIN/expired/revoked credentials fail closed; remote frame cannot call bridge; queued stale writes never execute |
| Physical iPad | Signed build, Pencil pressure/taps, palm contact, Scribble with actual language settings, rotation/window resizing, note recovery and foreground/background transitions |
| Streaming PC | Actual OBS/media actions and optional Windows extended display tested separately, including unplug/reconnect/network loss and hold cancellation |
| Distribution | Archive validation, required-reason/privacy manifest audit, App Store privacy/export declarations, internal TestFlight and reviewer hardware instructions |

Since 28 April 2026, App Store Connect uploads require Xcode 26+ with the iOS/iPadOS 26 SDK+. The SDK requirement does not force the app's minimum deployment OS to 26; select that minimum from tested compatibility. Xcode 26 requires macOS Sequoia 15.6+, and newer Xcode versions have their own compatibility matrix. A Windows-only run cannot verify an Apple archive. [Upload requirements](https://developer.apple.com/news/upcoming-requirements/), [Xcode compatibility](https://developer.apple.com/xcode/system-requirements)

Audit native calls and dependencies for required-reason APIs, such as applicable UserDefaults/file-timestamp access, and include only the approved reasons actually used in `PrivacyInfo.xcprivacy`. Do not copy an unrelated privacy manifest. [Required-reason APIs](https://developer.apple.com/documentation/bundleresources/describing-use-of-required-reason-api)

## Review preparation

Create a stable bundle ID before uploading; the Android package name and Windows installation paths need not change with the visible DeskWing branding. Apple app-record fields have different mutability rules, so settle bundle ID and SKU deliberately. [App information](https://developer.apple.com/help/app-store-connect/reference/app-information/app-information)

Store copy and review notes must distinguish native utility, remote control, preview and a true extended desktop. Explain the user-owned Windows PC and local-LAN requirement. Apple's remote-desktop rule 4.2.7 has conditions for clients mirroring particular software/services; it is not a blanket ban on second-monitor apps. The app must still provide substantive utility, accurate metadata and testable functionality. [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/)

Supply clear companion installation/pairing instructions, any required test resources and a camera-recorded physical-iPad demonstration for hardware-dependent behavior. An offline labelled demo can aid evaluation but must not hide a failing connection or stand in for actual hardware tests. [Complete review information](https://developer.apple.com/help/app-review/before-submitting-for-review/complete-review/)

Open prerequisites are Mac build access, signing team/account role, a physical iPad/Apple Pencil test path, final privacy/support URLs and accurate export/privacy declarations. They remain gates until verified; generated Swift files alone do not close them.
