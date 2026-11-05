# DeskWing

DeskWing is a local Windows companion and tablet workspace that combines a streamer deck, computer shortcuts, an ink-and-notes surface, and an optional secondary Windows display. The PC configures and reviews; the tablet keeps the working controls beside the user. The user selected **DeskWing** as the visible brand for the 1.14 line.

## People and use

The primary workflow is a person working or streaming at a Windows PC with a landscape Android tablet nearby. They need large controls, understandable connection state, useful reuse of existing boards, and a place to sketch or write without losing their current setup. The user's intended hardware is described as a Xiaomi Pad Mini with Xiaomi pen; exact model, Android version, firmware, keyboard, and physical behavior still require a device rehearsal.

Touch, pen, mouse, and keyboard must have practical paths through the relevant interface. The main control surface prioritizes touch; the PC editor prioritizes precise configuration and review. A compatible Android keyboard can convert handwriting into text, while typing and pasting remain available.

## Product commitments

- **Local control:** no cloud account is required for the core companion/tablet workflow. Keep connection and device access explicit.
- **Compatible upgrades:** retain installation identities, configuration locations, pairing, and the existing signing continuity. Branding alone does not create a new application identity.
- **Recoverable work:** make unsaved work, automatic backups, and conflicts visible. Keep reviewing a board separate from running its actions.
- **Honest capability:** distinguish a configured target from a rehearsed action, pressure support from a guaranteed pen experience, and a low-rate display view from full-motion remote desktop.
- **Focused surfaces:** each workspace should expose the controls needed for its task, with a clear route back to the other workspaces.

## Workspaces

| Workspace | Primary job | Boundary |
| --- | --- | --- |
| Stream Studio | Operate OBS, audio, Soundpad, apps, shortcuts, and macros through boards. | PC editing and read-only checks do not execute preview keys. |
| Computer Copilot | Use contextual shortcut and media layouts; choose Follow app or Pin layout. | Browser-site layouts remain manual; selecting a layout does not launch or focus an application. |
| Ink & notes | Draw with optional pen pressure, keep local notes, use keyboard handwriting, and export PNG. | Scratchpad data stays in that device's local app/browser storage and is not a shared PC/tablet notebook. |
| Second display | View a separately created Windows monitor on a paired tablet. | Optional installed-driver workflow; silent JPEG capture at up to four frames per second; no tablet input forwarding. |

## 1.14 scope

The interface includes grouped PC navigation, a visual board preview and key inspector, multi-selection, duplication and transfers, undo/redo, local recovery drafts, revision-conflict review, typed macro steps, live Soundpad views, direct Copilot Follow/Pin controls, read-only readiness checks, and restore-as-new-profile recovery. Ink adds a persistent local scratchpad, pressure-dependent width when reported by the device, a persistent finger-ignore toggle, erasing/history, PNG export, and Android native text entry for compatible handwriting keyboards.

The interface extends the incumbent dark studio palette, Manrope, line icons, cyan emphasis, and readable tablet keys. [DESIGN.md](DESIGN.md) records that system. [The DeskWing 1.14 guide](docs/DESKWING-1.14.md) records behavior, limits, and hardware rehearsal steps.

## Data and privacy

Saved boards and shared settings belong to the Windows companion. Local editor recovery drafts hold editable board content, not pairing or OBS credentials. Board exports and backups may contain labels, paths, shortcuts, and targets; users should review them before sharing. Sound files and applications remain references unless the user explicitly builds a portable pack that includes supported assets.

The scratchpad stores its drawing, notes, and finger-ignore preference on the device where it was created. Clearing application/browser data can remove it; exporting the drawing and copying notes are the user-controlled ways to take them elsewhere. A board backup is not a scratchpad backup.

DeskWing does not send ink to a remote OCR service or implement its own handwriting recognizer. The installed Android input method performs any recognition. Its vendor's privacy policy, language support, permissions, and offline availability apply; offline recognition is not guaranteed. The user chooses the keyboard and any export destination, which may itself be a cloud-backed document provider.

## Compatibility and release evidence

Visible branding is DeskWing. Existing names including `TabletDeck-Companion.exe`, `TabletDeck-Studio.apk`, Android package `app.tabletdeck.studio`, and `%LOCALAPPDATA%\TabletDeck` remain for upgrade compatibility. Source version is 1.14.0 and the Android version code is 17. Preserve these identities when documenting an upgrade.

The 1.14 work is development source until its specific artifact and release checks are recorded. A successful signed Android build is evidence about that artifact; it is not evidence of installation, pen behavior, store acceptance, or publication. iOS source is present, but a macOS/Xcode compile, signing, and device validation have not been performed in this Windows environment. Store preparation is tracked separately in [STORE-RELEASE.md](docs/STORE-RELEASE.md).

## Acceptance evidence

Automated checks should cover editing without execution, save conflicts, transfer validation, typed step validation, selective restore, input cancellation, and bounded storage. Rendered checks should cover desktop and tablet sizes, recovery/empty/error states, and focus behavior. Physical rehearsal must cover the actual tablet, pen, Android keyboard, sound routing, foreground application behavior, reconnects, and the optional virtual monitor before making device-specific reliability claims.
