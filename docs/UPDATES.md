# Windows and Android updates

Open **Windows options** in the desktop app. Windows and tablet updates are separate, so connecting a USB cable does not install anything unless you start an update or enable the optional automation.

The canonical repository is now [tugcantopaloglu/deskwing](https://github.com/tugcantopaloglu/deskwing). Current source checks that repository directly and verifies its exact release asset URLs. Binaries built before the rename use the former API endpoint with redirects disabled, so their in-app update checks no longer work; use [DeskWing releases](https://github.com/tugcantopaloglu/deskwing/releases) for manual downloads. Historical release tags, files, checksums, package IDs, and signing identities are unchanged.

## Update Windows from the app

1. Save any editor changes and finish running actions or held shortcuts.
2. Choose **Check for updates**. A newer stable release enables **Download Windows update**. If the installed version is current, the download/install controls stay disabled.
3. Download the update. The app verifies GitHub's release SHA-256, byte count, installer identity/version and embedded package checksums before enabling installation.
4. Choose **Install and restart**, then confirm. Tablet controls pause briefly and sounds played directly by the companion stop. The update does not stop OBS streaming or recording.
5. The helper waits for the old companion to exit, backs up replaced program files, applies the verified Windows files at the existing executable location and reopens the update settings with the previous installation result. Your shortcuts and firewall program path remain the same. Saved board data, pairing and certificate stay outside the program update.

Installation is blocked while a macro/action, held shortcut or tablet update is active. Unsaved desktop edits also block the install button. A failed handoff leaves the existing service running. If a file replacement fails after exit, the helper attempts to restore previous program files and retains recovery files. Reopening the shortcut is the fallback if automatic reopening fails.

Enable **Check for updates when the app opens**, then **Save desktop settings**, for checks at startup/window opening and every fifteen minutes while running. This contacts the project's public GitHub release API. It reports available versions; Windows installation still requires your explicit **Install and restart** confirmation. It does not silently restart a working studio.

The tray's **Check for updates** also reports the latest version. Use **Windows options** for download and installation. Closing the app window leaves downloads and the companion running in the tray; **Exit** stops them.

## Update the tablet over USB

1. Keep the complete Windows package, including `platform-tools`, beside the companion.
2. Connect a USB data cable, enable Android USB debugging and authorize this PC.
3. Open **Windows options → Update tablet over USB** and confirm.
4. Approve any Android installation prompt. The companion downloads the official release APK, checks its SHA-256/size, installs with `adb install -r --no-incremental`, and checks the installed package version afterward.
5. Open Tablet Deck again, or use the saved **Reopen Tablet Deck after a USB update** option. Its existing pairing and Wi-Fi/USB preference are retained.

For optional automation, enable **Update the tablet automatically over authorized USB** and save. It checks the selected/sole authorized physical tablet about once a minute. Release metadata is cached for fifteen minutes; a manual update requests fresh metadata. It skips already current/newer tablets and asks you to update Windows first if the APK requires a newer companion. Multiple tablets require selecting the intended USB serial in Windows options.

Automatic installation and automatic reopening are separate settings, both off by default. No emulator or wireless-debug endpoint is selected automatically. A verified APK waits if a running action, held keys, tablet tool/edit lease or Windows update makes installation unsafe. The cached APK is rechecked before installation. An Android installation failure pauses automatic installation retries for fifteen minutes; you can retry manually after resolving the prompt.

**Android briefly stops the app when replacing it.** Some manufacturers require installation approval even on an authorized debugging connection. This feature cannot bypass Android's restrictions or guarantee an uninterrupted screen. It does not clear app data, uninstall first, request a downgrade or disable signature checks. Keep the companion and cable connected until completion. Initial installation on a tablet without Tablet Deck can use the bundled `Install-APK.cmd` helper.

## Verification, recovery and manual alternatives

Downloads use only this project's stable GitHub release metadata and fixed installer/APK filenames. Redirects are limited to GitHub's release download hosts; HTTPS validation remains enabled. The installer also verifies its embedded package paths/hashes. Windows binaries remain unsigned: these checks establish integrity against trusted release metadata, not an Authenticode publisher signature.

Update cache and recovery copies live under `%LOCALAPPDATA%\TabletDeck\updates`. Previous files are retained for inspection/recovery; board snapshots remain in the normal backup history. Do not remove files while an update is running. See [profiles and backups](PROFILES-AND-BACKUPS.md) for board recovery.

If an update fails, read the status beside the action, fix the network/USB/installation condition and retry. **View release** opens the official release page. For a manual Windows update, use tray **Exit** before running the new installer. For a manual Android update, install the current release APK over the existing app with the same signing certificate.

The in-app Windows worker updates the Windows component and its ownership record; it does not automatically install Android or refresh every file at the package root. Use the USB updater or current release APK instead of assuming that an older extracted package's root APK has changed.

Developer instances launched with a custom data directory or HTTP/HTTPS/control ports use manual updates, followed by the same launch options. The in-app installer refuses to change those restart settings implicitly.

Tests cover mocked HTTPS/ADB failures and successful flows, browser/controller/API guards, and real installer replacement/rollback in an isolated temporary installation. They do not claim a live upgrade to a newer public Windows release or a real automatic Android installation. Development deployments and physical package checks are recorded in [LIVE-TEST.md](LIVE-TEST.md).
