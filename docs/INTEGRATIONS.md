# Soundpad, Discord, and other streaming tools

DeskWing includes direct Soundpad playback controls introduced in version 1.1. Other applications can use configured keyboard shortcuts, app/shortcut launches, or macros. OBS has its own direct integration.

## Compatibility at a glance

| Tool or workflow | Connection | What the board controls |
| --- | --- | --- |
| OBS Studio | Authenticated WebSocket v5 | Scenes, inputs, sources, filters, media, recording/streaming, replay, virtual camera |
| Soundpad | Official local remote-control named pipe | Search names/categories, import selected sounds on PC, play by index; microphone/speakers routing; stop, pause/resume, next, previous |
| Windows app audio | Windows Core Audio sessions | Per-app playback volume and mute by output/session |
| Discord desktop | User-configured keybinds | Toggle mute/deafen/streamer mode/screen sharing when the same keybind is configured in Discord |
| Discord voice audio | Soundpad microphone output | Effects reach the microphone device selected by Discord |
| Discord screen-share audio | Discord's selected audio capture | Must be configured in Discord; the board does not capture or mix it |
| Other tools with shortcuts | Keyboard shortcut action | Foreground shortcuts or the app's enabled global keybinds |
| Other Windows apps | Open Windows app / website / macro | Launch a configured executable, Windows shortcut, or URL |

The board is a controller. It does not install Soundpad, replace Discord's share picker, create a virtual audio device, or automatically integrate every third-party plugin.

## Soundpad

### Direct controls and library browsing

Dedicated actions use Soundpad's [supported remote-control interface](https://www.leppsoft.com/soundpad/en/rc/), independent of window focus. No executable path or custom shell command is needed. Soundpad must already be running on the PC under the same Windows account as the companion.

Both the standalone and Steam editions use Soundpad's remote interface; actual availability depends on the installed Soundpad version. Check [Soundpad's remote-control manual](https://www.leppsoft.com/soundpad/help/manual/tutorial/rc/) if an older installation rejects a command.

### Create a play button

1. Start Soundpad and add the sound files you want to use there.
2. Show its **All sounds** category and note the sound's index. Indexing starts at 1. The index belongs to that category, not necessarily the row in your currently selected category.
3. In the companion's PC editor, choose **Add button** and select **Soundpad · play sound**.
4. Set **Target** to the index, for example `7`.
5. Set **Soundpad output** to one of:

| Value | Output |
| --- | --- |
| `mic` | Soundpad's configured microphone only — useful for Discord voice |
| `speakers` | Local playback only |
| `both` | Local playback plus the configured microphone |
| `default` or blank | Soundpad's current play mode |

6. Give the button a label/icon and save.

Version 1.2 adds a **Soundpad** library window on PC and tablet. Search names, artists, and categories, choose an output, then press Play. On PC, select sounds and choose **Add selected buttons** to import them into a page. See the [controls guide](CONTROLS.md) for steps and limits. Manual index entry remains available.

Moving sounds within Soundpad's All sounds list can change their indices. Refresh the library and recheck affected saved buttons after reordering the list; new library imports resolve their current index before playback. Older/manual buttons need verified linking; see [Soundpad remapping](DESKTOP-AND-HOLD.md#soundpad-remapping).

### Playback controls and macros

Add **Soundpad · stop**, **Soundpad · pause / resume**, **Soundpad · next sound**, or **Soundpad · previous sound**. They do not need a target. Next/previous use Soundpad's playback behavior, including its previous play mode.

Soundpad actions can be macro steps. For a play step, Target is the sound index and the third field is `mic`, `speakers`, `both`, or `default`. The existing **Stop all deck sounds** action only stops the board's own WAV/MP3/WMA playback; use **Soundpad · stop** for Soundpad. A macro can call both stop actions if desired.

### Error handling

The companion waits for Soundpad's response rather than reporting success just because a process was launched. If Soundpad is closed, access is denied, or it does not respond within three seconds, the action reports an error. If Soundpad rejects an index or command, the board shows its status code. A success response means Soundpad accepted the command, not that another app's audio routing is correct.

## Discord streams and calls

Yes: you can operate the board while using Discord Go Live, screen sharing, or voice calls. OBS is optional for Soundpad, Windows shortcuts, and other non-OBS actions; its audio faders and stream badge still represent OBS state only.

### Control Discord with keybinds

1. Use the **Discord desktop app on the PC**.
2. Open **User Settings → Keybinds**.
3. Add a keybind for **Toggle Mute**, **Toggle Deafen**, **Toggle Streamer Mode**, or **Toggle Screen Share**, as available in your client.
4. Choose an unused combination. For example, configure `CTRL+ALT+F10` for Toggle Mute, `CTRL+ALT+F11` for Toggle Deafen, and `CTRL+ALT+F12` for Toggle Screen Share. These are examples, not Discord defaults.
5. Leave Discord's Keybinds settings page; its keybinds are disabled while editing them.
6. Add a DeskWing **Send keyboard shortcut** button with exactly the same combination.
7. Test while connected to a Discord voice channel. Check whether the configured keybind works globally in your client; foreground-only shortcuts need the intended app focused.

Discord documents desktop [custom keybinds](https://discord.com/blog/how-to-use-keyboard-shortcuts-on-discord-create-custom-keybinds) and [Toggle Screen Share](https://discord.com/blog/discord-patch-notes-november-1-2024). Keybind controls do not read Discord's current mute, deafen, or share state back into the board.

Starting screen share may still require selecting an application/screen and confirming settings in Discord. The board's built-in **Start stream** and **Record** actions control OBS, not Discord. Name your Discord shortcut button accordingly, such as **Discord share**, to avoid confusion.

The ordinary shortcut action sends a tap (press then release). For touch-and-hold push to talk, use **Hold keyboard shortcut / push to talk** with a matching Discord binding. See the [hold guide](DESKTOP-AND-HOLD.md#hold-to-talk-buttons). Windows can block injected shortcuts into apps running at a different elevation level; ordinary user-level apps are the starting point for setup.

### Make Discord hear Soundpad effects

1. In Soundpad, open **File → Preferences → Devices** and enable the microphone you use for Discord. Confirm Soundpad reports a working device setup.
2. In Discord **Voice & Video**, select that same microphone as the input device.
3. Use a Soundpad play button with output `mic` or `both`.
4. Ensure the microphone is unmuted and your voice-input mode permits transmission. Test with another person or Discord's input test.
5. If effects are cut off or filtered, follow [Soundpad's Discord troubleshooting guidance](https://www.leppsoft.com/soundpad/help/faq). Where applicable in your Discord version, select the Custom input profile and adjust noise suppression, echo cancellation, automatic gain control, and bypass-system-audio-processing settings as its guidance describes.

Soundpad's [device setup manual](https://www.leppsoft.com/soundpad/en/help/manual/config/) explains why the recording device must match. The board's normal **Play sound on PC** action only plays to the PC output; it does not inject sound into Discord's microphone.

### Game audio, shared audio, and echoes

Discord chooses audio separately for application sharing and screen sharing. Sharing one application does not automatically include every other process's sound. See Discord's [screen-sharing overview](https://discord.com/blog/how-to-stream-to-discord-from-desktop-or-mobile).

Soundpad microphone output goes through the voice path. If the same effect also plays on speakers and your share captures those speakers/system audio, listeners may hear it twice. Choose `mic` for voice-only effects, or deliberately route your mix so each sound reaches Discord once.

### Use OBS scenes with Discord

You can select **OBS Virtual Camera** as the camera in Discord to use OBS's composed video in a call; see the [official OBS guide](https://obsproject.com/kb/virtual-camera-guide). The OBS virtual-camera control in this board starts/stops that video output; it does not send an OBS audio mix into Discord.

For a Go Live workflow, you can open an [OBS projector window](https://obsproject.com/kb/power-of-projectors) and manually select that window in Discord's share picker. Scene changes then alter the projected video. Configure audio separately and rehearse the result. Neither workflow creates an automatic Discord broadcast integration or guarantees capture behavior on your device/client version.

## Other stream tools

**App volumes** lists Windows playback sessions and controls their volume/mute separately from OBS input faders. Start audio playback if an app is absent and refresh after changing its output device. An app can have several rows. These changes affect Windows playback levels and may alter audio captured by OBS or Discord; they do not adjust microphone gain. See the [controls guide](CONTROLS.md).

For a tool that offers a keyboard shortcut or global hotkey, configure the tool first, then create a matching **Send keyboard shortcut** button. App launches can be combined with shortcuts and delays in a macro, but starting an app does not automatically focus its window.

Tools without usable shortcuts may need a separate native integration or an explicitly configured Windows shortcut. There is no generic arbitrary-command or HTTP-request action in this release. Share the specific tool and desired control when requesting another integration.

## Verification limits

Soundpad command construction, routing, command-injection rejection, pipe framing, fragmented responses, and error statuses are tested against isolated fixtures. The physical rehearsal also verified Soundpad4.0.35 library data, Stop, speakers-only playback with audible user confirmation, and Android Wi-Fi/USB connections. Discord app launching and Windows playback-session mute/restoration passed. Discord microphone/deafen keybind delivery, microphone routing, listener audio, and live screen sharing still need rehearsal with the user's chosen configuration. See the [physical test report](LIVE-TEST.md) for scope and limits.

## Voicemeeter

Version 1.3 adds direct Strip/Bus gain and mute through the installed x64 remote DLL. See the [native integration guide](DESKTOP-AND-HOLD.md#native-voicemeeter-actions) for parameters and live-test limits.
