# DeskWing 1.14 user guide

DeskWing brings Stream Studio, Computer Copilot, Ink & notes, and Second display into one workspace selector. On Windows, the sidebar groups work into **Create**, **Operate**, **Connections & setup**, and **Settings & recovery**. Configure boards on the PC, then use the tablet for controls, writing, and the optional display view.

This guide describes 1.14.0 development behavior. It does not certify a published release or completed hardware rehearsal. Existing executable, APK, package, and configuration identities retain their TabletDeck names so upgrades can keep the established installation and pairing.

## Build and edit a board

1. In the Windows sidebar choose **Board editor**. Choose a page above the preview, or **Add page**.
2. Select a preview key to open its inspector. Preview keys never run their actions. Use the key checkboxes, or Ctrl/Command-click, to select several keys.
3. Edit the label, action, target, icon, or color in the inspector. Multiple selection exposes shared style and duplication tools.
4. Use the drag handle to reorder keys within the displayed group. Choose **Studio · 12 keys**, **Full pad · 24 keys**, or **Full pad · 40 keys** to review different preview densities; this choice changes the preview, not the saved tablet layout setting.
5. Use **Previous** and **Next** to inspect another group when the page has more keys. Choose **Save changes** to apply the edited board to the active profile and sync the saved board to the tablet.

The editor supports up to 30 pages and 120 manual buttons per page, within the companion's configuration-size limit. Live sources have their own limits. Duplicated keys and pages receive new identities so they can be edited independently.

**Page settings and transfers** contains the page source, duplication/deletion, OBS scene import, Soundpad browsing, and selected-key transfers. A page can use manual buttons, live OBS scenes, or the live Soundpad library. Live previews are read-only. Save a changed live-source choice before expecting its current keys in the preview. Previously saved manual buttons return when the page goes back to **Manual buttons**.

## Undo, drafts, and save conflicts

**Undo** and **Redo** apply to the current board-editing session. The editor retains a bounded history of up to 60 snapshots, grouping rapid typing in the same field. Ctrl/Command-S saves. Ctrl/Command-Z, Shift-Ctrl/Command-Z, and Ctrl/Command-Y control editor history when focus is outside a text field; inside a text field, normal text editing keeps its own shortcut behavior.

The save bar tells you whether work is saved, unsaved, or saving. Unsaved editable board content is also kept as a recovery draft in that PC web view/browser's local storage. This is separate from the companion's saved board and automatic backup history. It does not copy OBS passwords or pairing credentials. It may contain the labels, paths, targets, and shortcuts you are editing.

When the editor finds a recovery draft, choose **Review draft**, **Export draft**, or **Discard draft** before editing further. Reviewing does not immediately overwrite the saved board. If the saved revision has changed since the draft began, the conflict panel lists differing pages and the current saved page summary:

- **Load saved board** discards the current draft after confirmation. Export it first if you want a separate copy.
- **Export my draft** keeps a JSON copy for later review.
- **Use my reviewed draft** accepts that draft as the replacement candidate. You still need **Save changes** to apply it.

There is no automatic page-by-page merge. Review all affected pages before replacing the current saved pages. Current connection settings and credentials remain outside the board draft.

A storage warning means the local recovery copy could not be written. Export your board before closing. Unsaved navigation asks whether to discard; explicitly discarding removes the draft. A recovery draft is most useful after an interruption, not as a substitute for saving.

## Reuse keys and transfer boards

Select manual keys, open **Page settings and transfers**, and choose **Copy or move selected keys**. Pick a destination profile and manual page.

| Destination | What happens | Recovery |
| --- | --- | --- |
| Another manual page in the current profile | Copy or move changes the current draft; save to apply. | Use editor Undo before saving, or a board backup later. |
| A manual page in another profile | Save the source draft first. After confirmation, the transfer saves immediately with revision checks. | Original profile content is backed up. This is not an unsaved editor operation. |

Copy retains the source keys and creates new identities at the destination. Move removes the selected source keys. Live-source pages cannot receive manual transfers. If a profile changes while choices are open, reopen the transfer choices to load current revisions.

**Export** downloads the current profile's board JSON. **Import** replaces its draft after review; **Save changes** applies it. Export excludes OBS passwords, tablet credentials, inactive profiles, and Soundpad favourites. Referenced sound files and applications are not included. Use [portable packs](STUDIO-TOOLS.md) when you intentionally need supported audio assets with a board.

## Compose typed macros

Choose a macro action in the key inspector. Add an action or delay, then choose its type. Each type exposes meaningful fields: exact OBS scene/input/source/filter names, sound or app file paths, website URLs, shortcuts, Soundpad index/output, or supported Voicemeeter parameters.

- **Find OBS names** fills suggestions from the current connection. It does not change OBS.
- **Choose sound file** and **Choose app file** select paths without playing or launching them.
- **Check step** validates the supplied target without executing it. A valid path or name does not prove live routing or shortcut behavior.
- **Earlier**, **Later**, drag, **Duplicate**, and **Remove** edit step order and content. Drag stays within the same group.

Each group supports up to 30 steps and the macro may use three step levels. A delay is an integer from 0 to 10,000 milliseconds. The summary reports planned delays **plus action time**, so it is not an exact runtime promise. Hold shortcuts cannot run inside a macro; use a tap shortcut or a separate hold button.

Required confirmations are collected before execution. Stream and recording actions always require confirmation. A failed step stops the macro; a running action can be cancelled from its visible execution bar. Rehearse shortcuts in the intended foreground app and verify audio routing before using the macro live.

## Use live Soundpad views

On a page following the live Soundpad library, **All**, **Favourites**, and **Recent** sit directly above the keys, beside search and category filtering. Switching views keeps you on the pad. Search and category choices narrow that view; changing the live page resets these filters.

Star sounds in the Soundpad library to populate Favourites. Recent contains the last 30 distinct linked sounds accepted by Soundpad and is shared across controllers. Missing or ambiguous sound identities are not substituted with another sound. The pad explains whether the list is loading, unavailable, empty, or filtered to no matches. Keep Soundpad open and review the PC library when a file has moved or an identity is duplicated.

The selected output still depends on Soundpad's configuration and the receiving app's microphone settings. A successful command is not proof that viewers or a call participant heard the sound.

## Follow an app or pin a Copilot layout

In **Computer Copilot**, **Follow app** lets the layout follow supported foreground Windows applications. **Pin layout** keeps the current layout when you switch apps. Manually choosing a layout pins it; choose Follow app to resume automatic selection.

Browser-site layouts stay manual because DeskWing does not inspect website titles. Selecting a layout does not launch or focus an application. Copilot must be enabled in its PC settings, and shortcuts must match the intended application's bindings. Read the foreground-app and layout state before sending a command. See [Computer Copilot](COMPUTER-COPILOT.md) for the underlying controls and configuration.

## Check readiness and recover one profile

From **Operate → Readiness check** on Windows, inspect saved targets in the current profile. The check reads available OBS names, local file references, and Soundpad identities. It does not start a stream, press a shortcut, launch an app, or play a sound. It checks the saved profile, not unsaved editor changes.

Use **Needs attention** or **All checks**. Results show **Fix**, **Review**, or **Checked** with an explanation. **Edit button**, **Edit page**, or **OBS setup** takes you to the relevant configuration. A dependency timeout remains a follow-up item. Run **Check again** after saving a fix. Finish with a real rehearsal: static checks cannot verify the receiving app, audio route, device pressure, or a live broadcast outcome.

For recovery, open **Settings & recovery → Backups**, choose a snapshot's **Preview**, then use **Recover one profile**:

1. Choose a profile from the snapshot and give its recovered copy a name.
2. Choose **Restore as new profile** and confirm.
3. Open Profiles and switch to the recovered profile when ready to review it.

The recovered copy is inactive. Existing boards, favourites, and connection settings stay in place. It requires an available slot within the 10-profile limit. No action runs as a result of recovery.

The separate **Replace the whole configuration** disclosure shows the full-restore comparison: profiles, pages, buttons, favourites, and changed settings. That operation replaces current profiles and favourites after confirmation and keeps a backup first. Current pairing, certificate, and OBS password are preserved. If comparison is unavailable, refresh it before a full restore.

The latest 20 snapshots remain on the PC. They reference sound and application files; they do not copy them. Local editor recovery drafts and scratchpad data are separate from this history. See [Profiles and backups](PROFILES-AND-BACKUPS.md).

## Write and draw in Ink & notes

Choose **Ink & notes** from the workspace selector or Windows sidebar. The drawing tools stay above the paper: **Pen**, **Eraser**, ink color, width, **Undo**, **Redo**, **Clear**, and **Ignore fingers on canvas**.

Pressure changes line width when the pen and WebView/browser report pressure. Mouse and unsupported-pressure input use a normal fallback width. Select Eraser when the device does not expose a pen eraser button. Undo/redo applies to drawing strokes in the current session, including clearing the drawing; it does not provide a persistent cross-launch history for notes.

**Ignore fingers on canvas** starts enabled and is saved with the scratchpad. It prevents touch pointers from drawing on the canvas while allowing pen and mouse input. Disable it when you want to draw with one finger. The app also suppresses touch events around reported pen activity. These are software guards based on the device's input events, not a guarantee of complete hardware palm rejection. Rehearse with the actual pen, hand posture, and tablet; the switch does not turn every screen into a pen-only interface.

Completed strokes save immediately; notes save after a short typing pause. The status line reports success or a local storage error. Returning to this workspace or reopening the app restores the locally saved drawing, notes, and finger-ignore choice. Each device/web view/browser storage location has its own scratchpad: PC and tablet copies do not synchronize.

This is a bounded scratchpad: up to 1,200 strokes, 60,000 sampled points, 8,000 note characters, and a 3.5-million-character serialized storage guard. Once full, export before clearing. If saving fails, keep the view open, export the drawing, and copy the notes before leaving. If existing saved data is unreadable, it is kept unchanged until you explicitly choose **Start a new scratchpad**.

**Export drawing** makes a 1600×1000 PNG with the paper background and current committed strokes. Lift the pen first. On Android, choose a destination in the system document picker; in a browser the download starts normally. Export leaves the local scratchpad unchanged and excludes text notes. **Copy notes** copies the text, or selects it for your keyboard's Copy command if clipboard access is unavailable. Clearing app/browser data or uninstalling may remove the local scratchpad, so keep separate exports when needed.

## Convert handwriting to text on Android

Use **Handwrite text** beside Notes, or the **Handwrite** shortcut offered for an eligible focused text field in the Android app. DeskWing opens a native text field containing the current text. A compatible installed Android keyboard may recognize pen writing there. Use **Use text** to insert the result, **Cancel** to keep the original, or **Use keyboard** to type instead.

The current native capability check uses Android's stylus-handwriting API on Android 14 and newer. Android version, keyboard, language support, and pen behavior determine availability. When support is not reported, the dialog explains the typing/pasting fallback. The app does not guarantee handwriting on every device, and the browser/Windows view does not supply this Android panel.

Handwriting is text entry, not automatic recognition of the scratchpad drawing. If the destination field changes while the panel is open, DeskWing keeps the newer field rather than silently replacing it. Password fields are excluded, and the field's length limit still applies.

**Privacy:** DeskWing does not send these strokes to its own remote OCR service. The installed keyboard performs recognition under that keyboard vendor's privacy policy, permissions, and settings. Its offline support is not guaranteed. Choose and configure a keyboard appropriate for the text you enter. The exported PNG destination may also be a cloud-backed provider if you select one in Android's picker.

## Use Second display within its limits

Second display exposes the existing optional independent-monitor workflow. It requires a compatible virtual display driver already installed on Windows. The local PC console creates the monitor after explicit installed-driver acceptance, and PC sharing must be enabled before the tablet can receive frames. If Windows has the monitor disconnected or duplicated, use Windows display settings to make it an extended, nonoverlapping desktop.

Move a reference window or tool panel onto that monitor using Windows. The tablet view is **silent JPEG capture at up to four frames per second**, with 640/960/1280-pixel capture widths and a 384 KiB per-frame bound. The monitor's Windows refresh rate does not increase this transport rate. Busy frame requests are dropped rather than queued. It is suitable for relatively static panels and references; it is not a full-motion video transport.

There is no touch, pen, or pointer forwarding from this view into Windows, and no audio transmission. Protected content may appear black. The app does not install a driver, elevate, or silently use a physical monitor as a fallback. Pause the tablet view when finished; turn off PC sharing to revoke new frames, or stop the display to remove the monitor owned by the current session. See [Extended display](EXTENDED-DISPLAY.md) for driver, topology, and cleanup details.

## Hardware rehearsal checklist

Record the exact build, PC/Windows version, tablet model, Android/WebView version, pen model/firmware, and keyboard/language. Mark each result with observed behavior, not only a pass/fail label. Automated tests and rendered screenshots do not replace these checks.

- [ ] Install the signed 1.14 candidate over the prior Android app and verify pairing/settings survive. Confirm the installed version and signer before calling this an upgrade test.
- [ ] Open the Windows companion and reconnect by Wi-Fi and USB. Lock/unlock, background/resume, rotate, disconnect, and reconnect the tablet; verify no duplicate actions or stuck held shortcuts.
- [ ] Edit and save a harmless manual key, undo/redo changes, restart with an unsaved draft, and review a deliberate second-client save conflict without losing either export.
- [ ] Copy and move disposable keys within and between profiles; inspect backups. Recover a single profile as an inactive copy before trying any full restore.
- [ ] Check a macro containing harmless actions and a delay, then rehearse its runtime, confirmation, cancellation, and failure behavior. Verify audio in the actual receiving app.
- [ ] Test Soundpad All/Favourites/Recent with search, categories, missing sounds, and an unavailable source. Confirm the played sound and chosen output.
- [ ] Switch supported PC apps with Follow app, pin a layout, select a browser layout manually, and verify the foreground guard for shortcuts.
- [ ] Draw slow and fast strokes, vary pressure, erase, undo/redo, and clear/undo. Rest a palm on the canvas and near controls with finger-ignore enabled, then try deliberate finger drawing with it disabled.
- [ ] Rotate, background, lock, and resume during a stroke. Confirm no stray line appears and completed ink persists. Relaunch, export a PNG through the Android picker, cancel an export, and copy notes.
- [ ] Try handwriting in Notes and another eligible field with the actual keyboard/language. Confirm Use text, Cancel, Use keyboard, and unsupported-keyboard fallback. Test offline behavior only if an offline claim is needed, with that keyboard's configuration recorded.
- [ ] Check readable state and controls at desktop 1280×720/900×620, tablet 1280×800/800×600, and 390px narrow fallback, including keyboard focus and reduced motion.
- [ ] If using Second display, verify its distinct Windows monitor, explicit sharing, real tablet pixels, pause/resume, sharing revocation, and monitor removal. Confirm silent low-rate viewing and the absence of touch forwarding.

## Platform and release status

The source brand/version is DeskWing 1.14.0. `TabletDeck-Companion.exe`, `TabletDeck-Studio.apk`, Android package `app.tabletdeck.studio`, and `%LOCALAPPDATA%\TabletDeck` retain their compatible identities. Android uses version code 17. A signed build must still be installed and rehearsed on the target device before making hardware claims.

iOS source exists, but this Windows environment has not compiled, signed, or run it with macOS/Xcode. Source presence does not establish a working iOS release. App Store/Play Store submission materials and account/signing requirements remain in [the separate store preparation guide](STORE-RELEASE.md); this user guide does not imply store publication or approval.
