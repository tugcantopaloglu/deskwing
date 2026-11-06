# DeskWing 1.14 user guide

DeskWing brings Stream Studio, Computer Copilot, Ink & notes, Pen tablet, and Second display into one workspace selector. On Windows, the sidebar groups work into **Create**, **Operate**, **Connections & setup**, and **Settings & recovery**. Configure boards and pen targets on the PC, then use the tablet for controls, writing, Windows pen input, and the optional display view.

| What you want | Workspace | Where the result lives |
| --- | --- | --- |
| See the same sketch or notes on the tablet and PC | **Ink & notes** | DeskWing's shared scratchpad, with local backup and PNG export. |
| Draw inside Whiteboard on the web/in Teams, Photoshop, or another Windows pen-aware app | **Pen tablet** | The chosen Windows application's document; save it in that application. |
| See a secondary Windows desktop on the tablet | **Second display** | Native H.264 or an explicit JPEG compatibility view; neither forwards input. |

This guide describes 1.14.0 development behavior. It does not certify a published release or every hardware/application workflow. Specific observed checks are recorded below. Existing executable, APK, package, and configuration identities retain their TabletDeck names so upgrades can keep the established installation and pairing.

**Resumed work, 6 November 2026:** native H.264 Second display is experimental on `codex/display-high-refresh`. The earlier Android candidate crashed on launch during TLS cleanup on the UI thread; the checkpointed cleanup changes are unfinished and require build, lifecycle, and device validation. The user has resumed this work and reported missing Soundpad buttons, which are under investigation. The working pen build remains the installed tablet baseline until a replacement is verified.

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

**Ignore fingers on canvas** starts enabled and is kept as a device-local preference. It prevents touch pointers from drawing on the canvas while allowing pen and mouse input. Disable it when you want to draw with one finger. The app also suppresses touch events around reported pen activity. These are software guards based on the device's input events, not a guarantee of complete hardware palm rejection. Rehearse with the actual pen, hand posture, and tablet; the switch does not turn every screen into a pen-only interface.

Completed strokes save to a local backup immediately; notes save after a short typing pause. Returning to this workspace or reopening the app restores that local copy first. A separate sharing status shows whether drawings and notes have reached the companion. Open **Ink & notes** on the paired PC to see the shared document there. The PC can edit the same document with its mouse or available pen input.

This is a bounded scratchpad: up to 1,200 strokes, 60,000 sampled points, 8,000 note characters, and 3.5 MB of serialized UTF-8 document data. Once full, export before clearing. If local saving fails, keep the view open, export the drawing, and copy the notes before leaving. If existing local saved data is unreadable, it is kept unchanged until you explicitly choose **Start a new scratchpad**.

**Export drawing** makes a 1600×1000 PNG with the paper background and current committed strokes. Lift the pen first. On Android, choose a destination in the system document picker; in a browser the download starts normally. Export leaves the local scratchpad unchanged and excludes text notes. **Copy notes** copies the text, or selects it for your keyboard's Copy command if clipboard access is unavailable. Clearing app/browser data or uninstalling may remove the local scratchpad, so keep separate exports when needed.

### Share with the PC and resolve versions

Sharing runs while Ink & notes is active and visible; it pauses when the view is hidden or the native app is suspended. A visible desktop window can continue sharing after it loses keyboard focus. Sharing sends committed strokes and note text through the existing authenticated companion connection; an unfinished stroke is not a live broadcast. The companion keeps one shared scratchpad in `%LOCALAPPDATA%\TabletDeck\ink-workspace.json`, separate from board profiles and backup snapshots. Other paired controllers can access that same companion scratchpad.

Read both status messages: **Saved on this device · local backup kept** does not by itself mean the PC has the latest copy. **Shared with your PC** confirms the compared/saved version. **Offline · local copy kept** lets you continue working locally; use **Retry sync** or return after reconnecting. **Sync now** requests an immediate check. The finger-ignore preference remains specific to each device.

When only one side has changed since the last shared version, the app can send or load that change. When both sides differ, it shows **Two versions need your choice**, with stroke and note counts, and does not automatically merge or overwrite the drawings:

- **Use PC version** loads the PC drawing and notes after confirmation, keeping the current local version in the local recovery slot.
- **Use this version on PC** replaces the PC drawing and notes after confirmation and a revision check, keeping the former PC version in the local recovery slot.
- **Recovery copy** opens that saved local version for review. The current local version takes its place in recovery; you must explicitly choose before replacing the PC copy.

The recovery slot is one local alternate version, not a full drawing history. Export valuable versions first. A storage failure blocks a replacement that cannot preserve the alternate copy. If the PC copy changes again before an accepted replacement reaches it, refresh and review the new version. Loading a different document starts a new drawing undo history.

This sharing does not paint inside Whiteboard or Photoshop. Those applications need the separate **Pen tablet** workflow below.

### Use the scratchpad fullscreen

Choose **Fullscreen** for a paper surface without a reserved header, toolbar row, or outer padding. Small floating **Show tools** and **Exit fullscreen** buttons remain available. An amber attention mark on Show tools signals an offline, conflict, or save problem. Open Show tools for Pen/Eraser, width/color, undo, notes, export, saving/sharing status, and any conflict controls; choose **Hide tools** or return to drawing to close that overlay. **Show notes**/**Hide notes** adds or removes the notes area.

The drawing keeps its existing 8:5 ratio so saved strokes do not distort. On a 16:10 tablet it can fill the immersive screen; on other ratios, any unused bands use the paper color but are not drawable. There is no zoom or pan mode. Notes reduce the space available for paper when visible.

Choose Exit fullscreen to return. Escape closes the tools overlay first, then exits fullscreen. Workspace changes, backgrounding, and native tool dialogs leave immersive mode and cancel an unfinished stroke; completed ink is retained. Export remains available inside Show tools, including on narrow layouts. Open the tools overlay to check sharing or resolve a version conflict before assuming both devices show the same work.

In the Android app, fullscreen also requests that the system status and navigation bars hide and removes their WebView padding. An edge swipe can reveal transient system bars. Android Back exits immersive mode first; opening native handwriting/export or backgrounding restores normal UI, and returning does not re-enter fullscreen automatically. The iOS host requests hidden status bars, home-indicator auto-hide, and layout to the app window's bounds; it does not force Split View or Stage Manager to occupy the whole display. Home and edge gestures remain available. These are system-managed requests, not a permanent lockout of OS gestures. If native bar hiding fails, DeskWing keeps the app-filling canvas and shows a message. Verify system-bar behavior on the actual device; a simulator policy test does not establish physical immersive behavior.

### Resize the PC and ink windows

Both the main **DeskWing** window and the separate **DeskWing Ink** window have ordinary resizable borders, minimize/maximize controls, and a 620×480 minimum window size. Drag an edge or corner to resize either window. Main DeskWing opens at 1280×860 by default; DeskWing Ink opens at 1000×740. Window geometry is not saved across newly created windows.

In the PC's Ink & notes view, choose **Open ink window**, or use **Open ink window** from the Windows tray menu. It opens the same shared scratchpad in an independent window without the main navigation/sidebar. Repeating the command brings the existing ink window forward. It is another view of the same drawing and notes, not a new document or a separate Windows pen-input receiver.

The separate window opens onto the fitted paper with floating **Show tools** and **Fullscreen** buttons. Show tools exposes drawing controls, export, sharing status, and conflict choices; an amber attention mark flags a sharing or save problem. Choose **Show notes** for the notes overlay, then **Hide notes** to close it. Drawing on the canvas or pressing Escape also closes the overlays. This view keeps the ordinary Windows caption and resize border; it does not enter fullscreen automatically. The drawing retains its 8:5 ratio as the window changes size.

Closing DeskWing Ink closes only that view. Open it again to load the current shared document and local recovery state. Closing the main DeskWing window continues to hide it to the tray; the companion keeps running. Check the save/share state before closing, particularly after disconnected editing or a conflict. The separate window uses the same synchronization and replacement rules as the main Ink & notes view.

The window needs the normal desktop/tray companion. A headless companion cannot open it and returns guidance to restart in desktop mode. Dimensions follow Windows DPI scaling; reopening an existing window preserves its current size, while creating a new one uses its default size.

## Draw directly in a Windows application

**Pen tablet** sends pen input to Windows instead of adding strokes to the DeskWing scratchpad. During an active session, it shows a preview of the selected PC target behind the tablet input surface, so marks made in that application can return in the captured image. A physical pen must be reported as pen input; finger and mouse events on this surface are ignored. Save the resulting drawing in the Windows application.

**Whiteboard compatibility, checked 5 November 2026:** Microsoft retired the standalone Windows, iOS, and Android Whiteboard apps on 16 October 2026. For work or school accounts, use Whiteboard on the web or in Teams. Select that Windows browser/Teams window as the pen target, open a test board, and choose its pen tool. Browser/Teams pen handling still needs a physical test; do not install the retired standalone app for this workflow. [Microsoft's retirement guidance](https://support.microsoft.com/en-us/whiteboard/retirement-standalone-microsoft-whiteboard-apps).

1. Open the intended Windows drawing app and a disposable test document. Choose its drawing tool and, where the app offers an input mode, use its Windows Ink path. Use a brush configured to respond to pressure if you want to test pressure.
2. Open **Pen tablet** in DeskWing on the PC. Under **Windows target**, choose a **Screen** or **Window** and select **Enable pen & live view**. If already enabled, **Use this target** applies a changed selection. This permission covers both pen input and the target preview, and resets when the companion closes.
3. A Screen target maps the pad across that Windows display. A Window target maps across the application's client area, including any toolbars inside it; keep that window visible, uncovered, in the foreground, and at a stable size. Arrange the window and focus the drawing app before starting.
4. On the paired tablet, choose **Pen tablet**, then **Refresh** if needed. Confirm the selected target and choose **Fullscreen** if desired. Tap **Start pen** in fullscreen, or **Start pen session** in the normal view, only when the PC target is ready.
5. Draw with the pen and watch the target preview or Windows app. The preview reflects captured application pixels; the coordinate/pressure readings describe input and do not verify brush behavior. Use **Stop pen** when finished, and save the document in the Windows app. **Back to controls** leaves this workspace. **Disable pen & live view** on the PC revokes the enabled target.

The PC preview begins only with your active pen session for the PC-enabled target. Before Start, merely opening Pen tablet does not capture the PC. A Window target captures that window's client area, including its own toolbars, without substituting an unrelated desktop crop if capture fails. A Screen target shares the selected display and its visible windows. Choose the target according to the pixels you want to see and share.

This is an adaptive silent JPEG preview, bounded to 1600 pixels on either side. The client spaces requests by at least 96 milliseconds, giving a nominal ceiling of roughly 10 frames per second; actual delivery can be lower. It is separate from the pen-input requests and may update more slowly than the PC drawing application. Only one preview capture runs at a time; slow requests do not accumulate a frame queue. Some applications or protected content may produce unavailable or black images. Pen input can remain available when capture fails, so check the actual PC if the preview is unavailable. This is not a full-motion video or end-to-end latency promise.

The target area reports **Loading the live PC view…**, **Live PC view**, catching-up/paused status, or **PC view unavailable** with guidance. Brief busy/rate-limit responses keep a recent image and retry without repeatedly opening fullscreen tools. A recent image is kept for at most 1.5 seconds from the corresponding request start; stale, invalid, or failed captures clear rather than remain indefinitely. A hard capture error reveals the tools once per session, and retries do not reopen a drawer you close. An unsupported window may require choosing its Screen target on the PC, which changes the captured scope to that entire display.

Stopping the session, backgrounding or leaving the workspace, or losing the connection clears the preview. Target and session checks discard frames captured after their authorization or geometry changes. DeskWing does not save preview frames to disk or keep a frame history; the drawing itself remains in the Windows application's document. Second display is a separate independent-monitor workflow with native H.264 viewing and its own four-frame-per-second JPEG compatibility option.

Outside fullscreen, the surface keeps the target's aspect ratio. Fullscreen defaults to **Fill tablet**, which maps the whole tablet area to the chosen PC target; shapes can stretch when those proportions differ. In **Show tools → Surface mapping**, choose **Match PC proportions** to preserve proportions with unused edges that do not control the pen. The preview follows the same mapping as the input area. Changing mapping stops an active session and clears its preview; start again after the layout settles.

Hover, contact, pressure, tilt, barrel-button, and eraser information are forwarded when the tablet exposes them. The Windows implementation creates a synthetic pen with pressure values from 0 to 1024; that numeric range does not establish the pen's physical sensitivity or guarantee a target app's pressure response. Unsupported hardware or application settings can produce uniform strokes. This is a Windows Ink input path, not a bundled Wintab tablet driver.

For a Photoshop pressure check, Adobe documents **Brush Settings → Shape Dynamics → Control: Pen Pressure** and explains its Windows Ink versus Wintab paths. A Photoshop configuration forced to Wintab needs review before testing this Windows Ink implementation. These settings are test preparation, not Adobe certification of DeskWing. See [Adobe's tablet support guidance](https://helpx.adobe.com/photoshop/kb/tablet-support-faq-photoshop.html).

A window target must stay focused and uncovered. Moving/resizing it, closing it, switching away, or an invalid target can end the session. For Screen targets, input goes to the ordinary Windows surface under each mapped point, so check the screen contents carefully before drawing. Apps running as Administrator are rejected; use an ordinary unlocked Windows desktop. The input service requires a 64-bit process on Windows 10 version 1809 or later, within DeskWing's overall Windows requirements.

Only one pen session can own the PC at a time. Connection failure, slow or stale input, tablet blur/backgrounding, native suspension, and workspace exit stop input and clear queued samples. The server's short lease expires if fresh activity/renewal stops. Sessions never restart automatically: correct the cause and tap **Start pen session** again. Completed drawing belongs to the target application; DeskWing does not replay a stopped input queue.

Immersive fullscreen removes the heading, footer, reserved toolbars, and outer padding. Floating **Show tools**, **Start pen**/**Stop pen**, and **Exit fullscreen** remain available over the input area. Show tools exposes the target, status, Refresh, and Surface mapping; it does not reserve permanent canvas space. Exit fullscreen to reach **Back to controls**. Entering or leaving fullscreen stops the current input session because the pad geometry changes; explicitly start a fresh session after the layout settles. This differs from the shared scratchpad, which saves its own committed drawing document.

The same native system-bar behavior applies as in Ink & notes: Android Back/native tools/backgrounding restore normal UI, and a new input session must be started deliberately. A native bar-hiding error leaves the full-window surface available with explanatory guidance in Show tools.

Whiteboard on the web/in Teams and Photoshop still need app-specific tests with the intended input settings and brush. On 5 November 2026, the user's Xiaomi tablet/pen produced receiver strokes with varying pressure and tilt; the user also confirmed pressure/fullscreen operation and that the later input-delay fix worked. After the preview correction, the user confirmed “pen tablet is good” and “Yes, image and fullscreen are stable.” **The corrected Pen tablet image and fullscreen are physically user-verified on that setup.** This does not establish an exact latency figure, all-edge calibration, complete palm rejection, universal drawing-app compatibility, or measured high-refresh Second display playback.

## Convert handwriting to text on Android

Use **Handwrite text** beside Notes, or the **Handwrite** shortcut offered for an eligible focused text field in the Android app. DeskWing opens a native text field containing the current text. A compatible installed Android keyboard may recognize pen writing there. Use **Use text** to insert the result, **Cancel** to keep the original, or **Use keyboard** to type instead.

The current native capability check uses Android's stylus-handwriting API on Android 14 and newer. Android version, keyboard, language support, and pen behavior determine availability. When support is not reported, the dialog explains the typing/pasting fallback. The app does not guarantee handwriting on every device, and the browser/Windows view does not supply this Android panel.

Handwriting is text entry, not automatic recognition of the scratchpad drawing. If the destination field changes while the panel is open, DeskWing keeps the newer field rather than silently replacing it. Password fields are excluded, and the field's length limit still applies.

**Privacy:** DeskWing does not send these strokes to its own remote OCR service. The installed keyboard performs recognition under that keyboard vendor's privacy policy, permissions, and settings. Its offline support is not guaranteed. Finished notes can synchronize to the paired PC. Choose and configure a keyboard appropriate for the text you enter. The exported PNG destination may also be a cloud-backed provider if you select one in Android's picker. The iOS source uses native text entry/Scribble; a simulator build does not verify real Pencil or handwriting behavior on an iPad.

## Use Second display

Second display creates an independent Windows desktop and shares only that owned extra monitor. It requires a compatible virtual display driver already installed on the PC, explicit driver acceptance, and tablet-sharing permission. DeskWing does not install the driver or silently replace the extra monitor with a physical-screen capture.

1. On the PC, open **Second display** and choose **Check display**. Review the installed driver's identity.
2. When creating the monitor, choose **Resolution**: 1280 × 800, 1600 × 900, or 1920 × 1080. Choose **Windows display refresh rate**: 60, 90, 120, 144, 165, or 240 Hz, subject to driver/Windows support.
3. Accept use of that installed driver and sharing of everything on the extra display, then choose **Create independent display**. If the monitor already exists, use **Allow tablet sharing**. Arrange it as an extended, nonoverlapping desktop in Windows and move the desired application windows onto it.
4. In the tablet app, open Second display, choose **Refresh rate → 60 Hz** or **Match tablet**, then choose **Start second display**. The native viewer fits the extra desktop without stretching its proportions, keeps unused edges blank, and leaves **Exit display** available.

There are two explicit viewing paths:

| View | What it sends | Where it works |
| --- | --- | --- |
| **Start second display** | A silent H.264 stream decoded by the native tablet viewer; target rate is 60 Hz or the current tablet rate. | A supported native host and decoder, with a sufficiently fast Windows source display. |
| **Open compatibility preview** | Silent JPEG still images at up to four frames per second. | The browser or native app when the slower preview is useful. |

A failed native start does not silently switch to JPEG. Choose Open compatibility preview yourself if needed. Its capture widths remain 640/960/1280 pixels, with a 384 KiB per-JPEG limit; the current view requests 960 pixels. Increasing Windows refresh rate does not raise this fallback's four-frame-per-second transport limit.

### Choose and verify the refresh rate

**Match tablet** uses the tablet's reported current refresh rate, not its advertised maximum. The interface shows both separately. On the user's tablet, ADB reported 120 Hz current and 165 Hz maximum; this establishes a device capability reading, not a video-playback result. At a current rate of 120 Hz, Match tablet requests 120 Hz.

The extra Windows monitor's actual rate must be at least the selected tablet target. A 60 Hz Windows source cannot supply a 120 Hz match request. If necessary, stop viewing and recreate the extra display on the PC at a sufficient rate; removing it moves its windows back to remaining displays. Android stops a Match session when the tablet's current refresh changes materially and requires a fresh Start. On iOS, watch the screen-cadence counter and restart to choose a newly measured target if conditions change.

Read the counters while content is moving. Android distinguishes **fps shown**, **fps received**, target Hz, and tablet Hz, with a **below target** indication. Its shown count comes from decoder rendering callbacks. The iOS viewer reports **fps received** and screen Hz; these do not measure displayed video frames. On an unchanged desktop, the viewer retains the last picture and labels **Still image**. Keepalive heartbeats do not inflate video counters. A configured target is not an achieved frame rate, and these counters do not measure end-to-end latency.

Windows prefers DXGI duplication of the exact owned display output and includes its cursor. A persistent GDI fallback is available only at 60 Hz; if high-refresh GPU capture is unsupported, choose 60 Hz or a compatible display driver. The built-in Media Foundation H.264 encoder remains software-based. PC capture/encoding, the connection, tablet decoder, resolution, power/thermal state, and display mode can limit delivery. Android checks hardware H.264 decoding at the chosen resolution/rate; try 60 Hz or a smaller PC display when the requested format is unsupported. Protected content may be black.

### Stop, privacy, and limits

Only one native display stream runs at a time. It is separate from pen-input and control requests and does not build an unlimited queue of frames. Everything visible on the shared extra monitor can reach the paired viewer. This feature supplies neither audio nor touch, pen, or pointer forwarding; **Pen tablet** remains the separate workspace for drawing into a selected Windows target.

**Exit display** closes the viewer while leaving the Windows monitor available. Backgrounding, disconnection, revoked pairing/sharing, changed monitor geometry/rate, decoder failure, or companion shutdown ends viewing; it does not resume automatically. **Pause tablet sharing** stops sharing, and **Remove extra display** removes the owned monitor. See [Extended display](EXTENDED-DISPLAY.md) for driver, topology, and cleanup details.

An isolated PC-side test with an animated fixture on an owned 165 Hz monitor measured warmed capture/encode rates of 60.01 fps at a 60 Hz target and 118.75 fps at 120 Hz. The test removed its own monitor afterward and left physical monitor topology unchanged. These measurements exclude network transport and tablet playback. The final H.264 build and sustained physical tablet delivery remain pending; neither the tablet's refresh capability nor the PC-side rates establish end-to-end 60+ fps.

## Hardware rehearsal checklist

Record the exact build, PC/Windows version, tablet model, Android/WebView version, pen model/firmware, and keyboard/language. Mark each result with observed behavior, not only a pass/fail label. Automated tests and rendered screenshots do not replace these checks.

- [x] The user physically confirmed the corrected Pen tablet experience, image, and fullscreen are stable on the tested Xiaomi tablet.

- [ ] Install the signed 1.14 candidate over the prior Android app and verify pairing/settings survive. Confirm the installed version and signer before calling this an upgrade test.
- [ ] Open the Windows companion and reconnect by Wi-Fi and USB. Lock/unlock, background/resume, rotate, disconnect, and reconnect the tablet; verify no duplicate actions or stuck held shortcuts.
- [ ] Edit and save a harmless manual key, undo/redo changes, restart with an unsaved draft, and review a deliberate second-client save conflict without losing either export.
- [ ] Copy and move disposable keys within and between profiles; inspect backups. Recover a single profile as an inactive copy before trying any full restore.
- [ ] Check a macro containing harmless actions and a delay, then rehearse its runtime, confirmation, cancellation, and failure behavior. Verify audio in the actual receiving app.
- [ ] Test Soundpad All/Favourites/Recent with search, categories, missing sounds, and an unavailable source. Confirm the played sound and chosen output.
- [ ] Switch supported PC apps with Follow app, pin a layout, select a browser layout manually, and verify the foreground guard for shortcuts.
- [ ] Draw slow and fast strokes, vary pressure, erase, undo/redo, and clear/undo. Rest a palm on the canvas and near controls with finger-ignore enabled, then try deliberate finger drawing with it disabled.
- [ ] Rotate, background, lock, and resume during a stroke. Confirm no stray line appears and completed ink persists. Relaunch, export a PNG through the Android picker, cancel an export, and copy notes.
- [ ] Open Ink & notes on both devices. Share a committed tablet stroke and notes to the PC, then a PC edit back to the tablet. Disconnect, edit offline, reconnect, create a deliberate two-version conflict, inspect both replacement choices and Recovery copy, and retain exports of valuable versions.
- [ ] Exercise scratchpad immersive Fullscreen, Show/Hide tools, Show/Hide notes, Exit fullscreen, Escape, rotation, and backgrounding. Verify system-bar behavior, drawing bounds, stroke position, floating exit, and the conflict controls inside the tools overlay.
- [ ] Resize and maximize both the main PC and separate DeskWing Ink windows. Confirm the same shared ink, no clipped controls at the minimum size, and correct close/reopen behavior without stopping the companion.
- [ ] In a disposable Whiteboard web/Teams board (work or school account) or Photoshop document, choose the exact Windows browser/app target, enable input, enter tablet fullscreen, focus the target, and start explicitly. Verify coordinate mapping, slow/fast strokes, pressure-sensitive brush response, tilt, eraser, and barrel-button behavior supported by the hardware/app.
- [ ] During a Windows pen session, compare Fill tablet and Match PC proportions, move/resize or cover the target window, change focus, disconnect, lock/background the tablet, change fullscreen, and stop input. Confirm release, no delayed/replayed strokes, and explicit restart. Test a second controller's inability to take an active session.
- [ ] Check the selected-target preview at the center and all edges with both mappings. Confirm that window capture shows only the selected client area and Screen capture shows the intended display. Test an unavailable/black capture, a slow connection, Stop, backgrounding, target changes, and reconnects; confirm stale pixels clear and capture never starts without an active session. Record preview freshness separately from pen-input response.
- [ ] Try handwriting in Notes and another eligible field with the actual keyboard/language. Confirm Use text, Cancel, Use keyboard, and unsupported-keyboard fallback. Test offline behavior only if an offline claim is needed, with that keyboard's configuration recorded.
- [ ] Check readable state and controls at desktop 1280×720/900×620, tablet 1280×800/800×600, and 390px narrow fallback, including keyboard focus and reduced motion.
- [ ] For Second display, verify the distinct Windows monitor and sharing permission. Compare 60 Hz and Match tablet with moving content, recording Windows source Hz, tablet current/max Hz, received fps, and Android shown fps where available. Test explicit JPEG compatibility viewing, exit/background/restart, sharing revocation, topology/rate changes, and monitor removal. Confirm no audio or input forwarding.

## Platform and release status

The source brand/version is DeskWing 1.14.0. `TabletDeck-Companion.exe`, `TabletDeck-Studio.apk`, Android package `app.tabletdeck.studio`, and `%LOCALAPPDATA%\TabletDeck` retain their compatible identities. Android uses version code 17. Rebuild and install a candidate containing native H.264 Second display before rehearsing it; an earlier 1.14 installation does not establish that this later change is present. Signed build, installation, and device/application behavior are separate pieces of evidence.

The stable main checkpoint `66d20d88b37a6a65349fd3534e469c3706b89d23` passed 58 Windows suites and 11 unsigned iOS simulator tests, including the final pen-preview corrections and canonical DeskWing repository update path. The user physically confirmed the corrected Pen tablet image and fullscreen are stable. This checkpoint excludes the resumed native H.264 display work; the Android launch crash, cleanup changes, and sustained tablet playback still need their own validation. Physical-iPad installation, Pencil/Scribble, live connection, signing, account flows, and store approval remain separate checks. [Stable Windows and iOS checkpoint](https://github.com/tugcantopaloglu/deskwing/actions/runs/42108057065).

App Store/Play Store submission materials and account/signing requirements remain in [the separate store preparation guide](STORE-RELEASE.md); this user guide does not imply store publication or approval.
