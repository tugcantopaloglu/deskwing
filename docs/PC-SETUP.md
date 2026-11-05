# Set up the Windows companion

Install the Windows companion, then open **DeskWing** from its Desktop or Start menu shortcut. Version 1.11 opens a standalone, resizable Windows application with its own taskbar icon. It has Overview, Board editor, Tablet connections, OBS setup, Profiles, Backups, Monitoring and Windows options in its sidebar. Closing the window hides it to the tray and keeps tablet controls running. Reopen it from the shortcut or tray to continue in the same window. **Stop companion** in the sidebar or tray **Exit** stops it.

The tray is the notification area beside the Windows clock. Check the **Show hidden icons** arrow if DeskWing is not immediately visible. Double-click its four-pad icon, or right-click it and choose **Open DeskWing**. Developer launches using `--no-browser --no-tray` deliberately disable both the window and tray; stop such a development instance and start normally before using the desktop window. `--tray --no-browser` starts with only the tray visible. The taskbar button belongs to the open app window; the tray remains after it closes. Startup with Windows remains opt-in.

The desktop window uses the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), which is already installed on many Windows PCs. If missing, the app explains how to obtain it from Microsoft. The app does not silently download an installer. Its interface stays local; public Twitch/Kick content is loaded only when you explicitly enable chat. Files exported from the local console use a Windows Save dialog. Remote chat downloads and hardware permission requests are blocked.

The Windows console opens its overview. The tablet keeps its control board, Full pad and arrangement tools. The older PC setup panel remains available through More tools for compatibility; the sidebar is the current entry point.

**Windows options** also contains verified Windows download/install controls and optional manual/automatic USB APK updating. The sticky Save button applies preferences; download/install actions have their own status and confirmation. See [Windows and Android updates](UPDATES.md).

## Pair a tablet

Choose **Tablet connections**. Install the APK on Android and enter the address, certificate code, and single-use PIN shown on the PC, or use Show pairing QR. PINs expire after five minutes; generate another if needed. Pairing is shared across profiles.

For Wi-Fi, keep both devices on the same local network and allow the companion through Windows Firewall on that private network. For USB, connect a data cable, enable USB debugging, and approve this PC on the tablet. Use `USB-Connect.cmd` from the Windows package, or enable **Reconnect USB automatically** in **Windows options**. Select USB on the tablet. With multiple tablets, set the tablet serial in Windows options. Pairing still requires the PIN/certificate check.

Overview separates saved pairings from active authenticated fast links and updates status every five seconds. USB status distinguishes an authorized device, established tunnels, and the installed app. Saved pairing does not prove a current connection; HTTPS fallback may work without a fast link. No connection count measures latency.

## Connect OBS

1. Open OBS Studio on the same PC. In **Tools → WebSocket Server Settings**, enable the server and keep authentication enabled.
2. In **OBS setup**, copy the port and password from OBS. The usual port is 4455.
3. Choose **Save OBS settings**. The companion saves and checks the connection. Connection checks do not start streaming, recording, playback, or other board actions.
4. Use **Find inputs** to load audio input names from the saved OBS connection. Choose exact names for the microphone and desktop-audio fields, then save again. A connected OBS server can still have a missing audio input; the check reports both separately.

A blank password field keeps the current saved password. Entering a new password replaces it. **Clear saved password** asks for confirmation and removes it for all profiles. Do not enter a replacement and choose Clear simultaneously. Saved passwords are never displayed back in this form. Choose **Check saved connection** to test persisted values; unsaved field changes are not used by that check or Find inputs.

OBS settings apply to every board profile. Saving here changes only the port, password choice, and audio input names; it preserves pages, actions, inactive profiles, favourites, and paired devices. Settings cannot change during a running macro. The normal automatic backup rules apply; snapshots exclude the password.

## Build your board

**Edit buttons** opens the existing board editor for apps, shortcuts, sounds, scenes, macros, and page arrangement. Changes sync after saving. **Manage profiles** creates or switches boards. **Backups and restore** previews a snapshot before confirmed restore. See [profiles and backups](PROFILES-AND-BACKUPS.md).

**Browse sounds** opens the Soundpad library. Soundpad must be running on the PC. Star favourites and choose playback output before adding buttons. For Discord, choose **Speakers + microphone** and select the same microphone in Soundpad and Discord. Discord voice processing can filter clips; rehearse with its local microphone test. The setup hub does not alter audio devices or Discord settings automatically.

**Windows options** contains Start with Windows, automatic USB reconnect, optional tablet serial, and update notifications. All are optional. Startup changes affect only the current Windows user. Update checks contact the project's public GitHub release API; downloads and installation remain manual. Save changes before leaving. If USB reconnect is enabled, its status updates within five seconds; choose **Refresh checks** to see the latest snapshot in the hub.

## If something fails

- **Old or locked browser tab:** reopen the control panel through the tray menu to get the current local admin session. Then choose PC setup or Retry checks.
- **OBS not connected:** check that OBS is open, its WebSocket server is enabled, and the saved port/password match. Save before finding inputs. OBS reconnects automatically.
- **Input not found:** use Find inputs and select the exact name. These faders control OBS inputs, not Windows master volume.
- **Cannot leave a form:** save changes, or Close and explicitly discard them. Setup navigation protects unsaved button, OBS, and Windows settings.
- **Status looks old:** use Refresh checks. Saved pairing count, USB tunnel status, and OBS state are separate facts; none proves touch-to-sound latency.

The new desktop flow has isolated API/controller regression coverage. Browser-rendered layout, real keyboard/screen-reader use, and physical tablet interaction were not checked during this development pass because app/browser automation was disabled by request. Use a rehearsal before relying on any setup change during a broadcast.
