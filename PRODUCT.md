# DeskWing

DeskWing is a local Windows companion and tablet workspace that combines a streamer deck, computer shortcuts, shared ink and notes, a Windows pen-input surface, and an optional secondary Windows display. The PC configures and reviews; the tablet keeps the working controls beside the user. The user selected **DeskWing** as the visible brand for the 1.14 line.

## People and use

The primary workflow is a person working or streaming at a Windows PC with a landscape Android tablet nearby. They need large controls, understandable connection state, useful reuse of existing boards, and a place to sketch or write without losing their current setup. The user's intended hardware is described as a Xiaomi Pad Mini with Xiaomi pen. The user has physically exercised the Windows pen path; exact hardware/firmware details and each additional app, keyboard, and lifecycle scenario still need to be recorded with their own results.

Touch, pen, mouse, and keyboard must have practical paths through the relevant interface. The main control surface prioritizes touch; the PC editor prioritizes precise configuration and review. A compatible Android keyboard can convert handwriting into text, while typing and pasting remain available.

## Product commitments

- **Local control:** no cloud account is required for the core companion/tablet workflow. Keep connection and device access explicit.
- **Compatible upgrades:** retain installation identities, configuration locations, pairing, and the existing signing continuity. Branding alone does not create a new application identity.
- **Recoverable work:** make unsaved work, automatic backups, and conflicts visible. Keep reviewing a board separate from running its actions.
- **Honest capability:** distinguish a shared drawing document from pen input into a Windows application, pressure support from a guaranteed app/device experience, and requested display refresh from measured video delivery.
- **Focused surfaces:** each workspace should expose the controls needed for its task, with a clear route back to the other workspaces.

## Workspaces

| Workspace | Primary job | Boundary |
| --- | --- | --- |
| Stream Studio | Operate OBS, audio, Soundpad, apps, shortcuts, and macros through boards. | PC editing and read-only checks do not execute preview keys. |
| Computer Copilot | Use contextual shortcut and media layouts; choose Follow app or Pin layout. | Browser-site layouts remain manual; selecting a layout does not launch or focus an application. |
| Ink & notes | Share the scratchpad drawing and notes with the paired PC, keep a local backup, use keyboard handwriting, and export PNG. | One shared document per companion, revision-checked replacement with explicit conflict review; no automatic stroke merge. Immersive paper preserves its ratio, with floating tools. |
| Pen tablet | Draw into a chosen Windows display or application window with reported pressure/tilt and a preview of that target behind the tablet input surface. | Explicit PC setup and an active tablet session authorize input and target preview. Fullscreen offers Fill tablet or Match PC proportions. App, brush, hardware, and capture support determine the result. |
| Second display | View a separately created Windows monitor through a native H.264 stream, with 60 Hz or Match tablet selection. | Optional installed-driver workflow and explicit sharing; actual source/decoder/connection limits apply. JPEG remains a separately chosen ≤4 fps compatibility view. No audio or tablet input forwarding. |

## 1.14 scope

The interface includes grouped PC navigation, a visual board preview and key inspector, multi-selection, duplication and transfers, undo/redo, local recovery drafts, revision-conflict review, typed macro steps, live Soundpad views, direct Copilot Follow/Pin controls, read-only readiness checks, and restore-as-new-profile recovery. Ink adds a shared PC/tablet scratchpad with local recovery, pressure-dependent width when reported by the device, a persistent finger-ignore toggle, erasing/history, PNG export, and native text entry for compatible handwriting input methods. Windows pen input separately sends real synthetic pen events to a mapped Windows target and displays a bounded JPEG preview of that target during an active session. Both pen surfaces offer fullscreen operation.

The main Windows console and an optional separate DeskWing Ink window are independently resizable and maximizable. The ink window presents the same shared document without the main navigation; floating tools and optional notes overlays leave the client area available for paper. It keeps its ordinary Windows caption and border, and closing it does not stop the companion. Immersive tablet layouts remove reserved toolbars and use floating controls, with native requests to hide system bars. System gestures, native dialogs, and lifecycle changes retain a route out. Preserving the scratchpad's paper ratio and mapping a whole pen-input area are separate design choices.

The interface extends the incumbent dark studio palette, Manrope, line icons, cyan emphasis, and readable tablet keys. [DESIGN.md](DESIGN.md) records that system. [The DeskWing 1.14 guide](docs/DESKWING-1.14.md) records behavior, limits, and hardware rehearsal steps.

## Data and privacy

Saved boards and shared settings belong to the Windows companion. Local editor recovery drafts hold editable board content, not pairing or OBS credentials. Board exports and backups may contain labels, paths, shortcuts, and targets; users should review them before sharing. Sound files and applications remain references unless the user explicitly builds a portable pack that includes supported assets.

The scratchpad keeps a local drawing/notes backup and a device-local finger-ignore preference. While Ink & notes is active and connected, the drawing and notes synchronize through the paired companion; open Ink & notes on the PC to see them. The companion stores one private `ink-workspace.json` beside its existing data. This is separate from board profiles and their backups. Offline changes remain local until sharing can resume. Divergent versions need a user choice; the replaced version is kept in a local recovery slot. This is not a multi-user live merge or an unlimited document history.

Windows pen input transmits bounded input samples to the paired PC during an explicit session. The chosen Windows application handles and saves resulting marks in its own document. Those marks do not automatically appear in the DeskWing scratchpad or its PNG export. A display target maps to the visible Windows screen area; an application target additionally checks that window's presence, foreground state, and bounds.

The active session also requests a silent JPEG preview of that same selected target, at up to 12 frames per second. A Window target captures that application's client area without falling back to a desktop crop; a Screen target captures the selected display, including its visible windows. The companion validates the session and target before and after capture, keeps no saved frame history, and does not write these images to disk. Stop/background/connection-loss paths clear the tablet preview. Preview availability and freshness depend on the target application and connection; the image is not a full-motion or latency guarantee. Second display remains a separate independent-monitor workflow, offering native H.264 video and an explicit four-frame-per-second JPEG compatibility view.

Second display shares everything visible on the owned extra monitor through a separately authenticated native video session. Match tablet uses its current refresh, not its maximum; the actual Windows source rate must meet the request. The viewer exposes received frame rate and device refresh, with Android also reporting decoder-rendered frames. These distinguish the configured target from observed delivery. The Windows encoder currently uses software Media Foundation H.264; neither a configured 60/120 Hz target nor an encoder-only timing check establishes sustained capture-to-tablet performance. Exit/background, sharing revocation, topology changes, or failures end viewing without automatic resume.

Whiteboard preparation must use the web or Teams route for work/school accounts: Microsoft retired its standalone Windows/iOS/Android apps on 16 October 2026. This account/platform availability does not certify DeskWing pen delivery. [Microsoft's current guidance](https://support.microsoft.com/en-us/whiteboard/retirement-standalone-microsoft-whiteboard-apps).

DeskWing does not send ink to a remote OCR service or implement its own handwriting recognizer. The installed Android input method performs Android recognition; the iOS source uses its native text-entry/Scribble path. The input provider's privacy policy, language support, permissions, and offline availability apply; offline recognition is not guaranteed. Finished note text can be shared with the paired PC. The user chooses the keyboard and any export destination, which may itself be a cloud-backed document provider. A target drawing application may also use its own cloud storage under its own settings.

## Compatibility and release evidence

Visible branding is DeskWing. Existing names including `TabletDeck-Companion.exe`, `TabletDeck-Studio.apk`, Android package `app.tabletdeck.studio`, and `%LOCALAPPDATA%\TabletDeck` remain for upgrade compatibility. Source version is 1.14.0 and the Android version code is 17. Preserve these identities when documenting an upgrade.

The 1.14 work is development source until its specific artifact and release checks are recorded. The user physically tested the Xiaomi tablet/pen through the Windows pen receiver on 5 November 2026 and reported pressure and fullscreen working, then confirmed that the input delay was fixed after the later input update. The receiver recorded varying pressure and tilt during the test strokes. These observations establish the tested input path and the user's delay improvement; they do not establish exact latency, edge alignment, Whiteboard/Photoshop behavior, or the new selected-target preview.

Windows regression and the unsigned iOS simulator build passed at commit `a9a85c93d01b6d03d79648644548431a53ee0efd`, including the selected-target pen-preview source. Physical preview alignment/freshness remain unverified. The later native H.264 display path requires its own final build and end-to-end device rehearsal. ADB reported 120 Hz current and 165 Hz maximum for the user's tablet; that is a device-capability observation, not a delivered-video result. Signed physical-iPad, Pencil, live connection, account, and store checks remain separate. [Windows and iOS validation run](https://github.com/tugcantopaloglu/tablet-deck-studio/actions/runs/42094855827).

Signed builds, successful installations, specific device tests, and store acceptance are separate evidence. Store preparation is tracked in [STORE-RELEASE.md](docs/STORE-RELEASE.md).

## Acceptance evidence

Automated checks should cover editing without execution, board and ink conflicts, transfer validation, typed step validation, selective restore, bounded storage, pen ownership/sequence/expiry, preview authorization/geometry/size limits, and lifecycle cancellation. Rendered checks should cover desktop and tablet sizes, fullscreen exit paths, target selection, preview loading/unavailable/cleared states, recovery/empty/error states, and focus behavior. Physical rehearsal must cover the actual tablet, pen, keyboard, shared ink, target drawing application and brush, pressure/tilt, preview alignment/freshness, sound routing, foreground changes, reconnects, and the optional virtual monitor before making device-specific or Photoshop/Whiteboard reliability claims.
