# Desktop, push to talk, updates, and integrations

Version 1.3.0 introduced the following controls. Version 1.4 adds the [low-latency transport and Wi-Fi/USB guide](PERFORMANCE.md). Update both the Windows companion and APK.

## Install or update Windows

Download **TabletDeck-Setup.exe** from the project's [releases](https://github.com/tugcantopaloglu/deskwing/releases/latest). Run it, review the displayed destination, then type `INSTALL`. The console installer verifies the embedded package's paths and SHA-256 checksums before copying files. It installs for the current account, without administrator access, to:

```text
%LOCALAPPDATA%\Programs\TabletDeckStudio
```

It creates a Start menu shortcut. It does not launch the companion or tablet app, enable startup, install drivers, change the firewall, or install the APK automatically. Open the shortcut yourself when ready; Android installation still uses the APK or Install-APK.cmd.

For updates, exit the existing companion through its tray menu first. The installer refuses to replace an unmanaged folder or files that are in use. A previous managed installation is retained in a timestamped sibling folder for recovery. If an update fails before the folder swap completes, the previous installation is restored. Remove retained backup folders yourself after confirming the update works. Application data remains separately under `%LOCALAPPDATA%\TabletDeck`.

The complete ZIP remains available for portable use. The installer and companion are unsigned Windows executables, so SmartScreen may prompt. Verify release checksums and download from the linked repository.

To uninstall the managed program files, run the downloaded setup executable from a terminal:

```powershell
.\TabletDeck-Setup.exe --uninstall
```

It removes only files recorded by the installer, its matching Start menu shortcut, and its matching startup entry. Untracked files and your board/pairing data are retained. Close the companion first. Uninstalling a portable ZIP means removing the extracted folder yourself; the managed uninstaller refuses an unrecognized folder.

## Tray and startup

The companion normally keeps running in the Windows tray. Double-click the tray icon or choose **Open control panel** to open its PC editor. Closing the browser does not stop tablet control. Choose **Exit** in the tray menu to stop the companion and release held keys.

Open **Edit → Desktop settings** to enable **Start with Windows**, **Reconnect USB automatically**, or **Notify about updates**. Save desktop settings; these are separate from board edits. Startup uses the current account's Windows Run entry and starts quietly with the tray available. All three settings are opt-in.

For command-line/server tests that must not open a browser or tray UI:

```powershell
.\TabletDeck-Companion.exe --no-browser --no-tray
```

`--tray --no-browser` starts the tray without opening the PC editor. The app does not install a Windows service.

## Automatic USB reconnection

Enable **Reconnect USB automatically** in Desktop settings or the tray menu. Keep the bundled platform-tools folder beside the companion. The companion checks authorized USB devices and restores ADB reverse tunnels for its HTTPS and fast control ports, approximately every five seconds.

USB debugging and the tablet's PC authorization remain required. It never opens an Android app. With multiple physical USB tablets, enter the intended serial in Desktop settings. Find it with `platform-tools\adb.exe devices`. Emulators and network ADB endpoints are excluded from automatic selection. Unauthorized or missing tablets produce a status message rather than choosing another device.

The tablet must still be paired and set to USB mode. Saved certificate pins and tablet credentials are reused. The existing USB-Connect helper remains available for manual operation. Automatic reconnection is only active while the companion runs.

## Update notifications

When enabled, the companion checks this project's public GitHub stable-release API every fifteen minutes. Checks send a versioned application user agent and ordinary network metadata; they do not upload your board, passwords, device records, or Soundpad library. **Check for updates** also runs on demand from Desktop settings or the tray menu.

The tray displays a notification when a newer stable version is found. Desktop settings shows the installed/latest version or a recoverable network error. **View release** opens the project's release page only when you choose it. Downloads and installation remain manual; the app does not silently replace executable files. Update both Windows and Android components when a release asks for it.

## Hold-to-talk buttons

1. In the target app, configure a push-to-talk key that it accepts globally, for example `F13`. Choose a key that does not conflict with a game or another app.
2. In the PC editor, add a button and choose **Hold keyboard shortcut / push to talk**.
3. Enter the same key or key combination in Target. Save the board.
4. Keep the tablet key pressed while speaking. Releasing sends key-up. A keyboard can use Space or Enter on the focused board key.

The board renews a short server-side lease while the key is held. Releasing, cancelling the gesture, backgrounding/losing focus, revoking the tablet, saving board configuration, or shutting down releases the keys. If the connection disappears, the lease expires within three seconds of its last successful renewal. Windows can block input into elevated apps; use matching privilege levels. The app retries key-up if Windows temporarily blocks it.

Only one hold lease can be active at once. Hold actions cannot be macro steps and cannot require confirmation. They send keyboard input, not microphone audio, and do not read Discord's mute/PTT state. Configure Discord's microphone and input mode separately, then rehearse transmission. Native Android touch, backgrounding, and real Discord delivery remain unverified in this release.

## Soundpad remapping

New library imports record a SHA-256 identity derived from the PC sound-file path. The raw path is not sent to the tablet. A linked button resolves its current All sounds index immediately before playback, so reordering the Soundpad list does not silently change the intended file. Library Play also includes that identity.

In the PC Soundpad library, **Refresh linked indices** updates saved index fields for linked buttons, including macro steps. If a linked file disappears or has duplicate library entries, playback is refused rather than falling back to the old index. Reimport the intended sound to repair it. Moving/renaming the sound file changes its identity, so reimport after that change too.

Buttons created before 1.3 or by manual index entry remain index-based until linked. First verify their current indices in Soundpad, then choose **Link old buttons** and confirm. The app cannot infer what an already-incorrect legacy index used to mean. Editing a play button's Target clears its old binding; reimport or relink it deliberately.

## Native Voicemeeter actions

Keep Voicemeeter, Banana, or Potato installed in its standard `Program Files\VB\Voicemeeter` location and running. Add **Voicemeeter · set gain or mute** in Edit. Target is a supported zero-based parameter and Value is a number:

| Target | Example Value | Result |
| --- | --- | --- |
| `Strip[0].Gain` | `-6` | First input strip to -6 dB |
| `Strip[0].Mute` | `1` | Mute first input strip |
| `Bus[0].Mute` | `0` | Unmute first output bus |
| `Bus[1].Gain` | `-12` | Second output bus to -12 dB |

Only Strip/Bus indices 0–7, Gain (-60 to +12 dB), and Mute (0 or 1) are accepted. Available indices depend on the edition; an unsupported channel returns an error. Actions can be macro steps. There is no arbitrary Voicemeeter command/script facility. The companion loads the installed x64 remote DLL on demand and does not launch Voicemeeter or redistribute its DLL.

This uses the publisher's [official remote API](https://github.com/vburel2018/Voicemeeter-SDK). The command allowlist is tested, but actual Voicemeeter operation and routing have not been tested against a live installation.

## Rehearsal still required

The 1.3 development session used command-line tools, fake input sinks, JavaScript logic tests, a mock OBS protocol server, and an isolated installer test. No browser/GUI or streaming application was opened. Tray appearance, startup after a real login, physical USB reconnection, tablet rendering/touch/sleep, live Soundpad/Voicemeeter, and Discord audio still need testing on your setup. Passing protocol tests does not verify a real broadcast.
