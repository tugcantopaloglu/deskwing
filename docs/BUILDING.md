# Build and test

Use a prebuilt release if you only want to use the app. These instructions are for Windows x64 development.

## Prerequisites

- .NET SDK 10.0 or newer for the companion; Windows 10 version 2004/build 19041 or newer. The Windows SDK .NET reference pack is pinned to 10.0.19041.57 and restored by the SDK for the media-session APIs.
- **PowerShell 7**, invoked as `pwsh`, for the APK build. Windows PowerShell 5.1 can run the release USB/install helpers, but the APK script uses modern .NET cryptography APIs.
- JDK 17, Android platform 36, and Android Build Tools 36.x for the APK. Pinned downloads currently use Build Tools 36.1.0.
- Node.js 22+, Chrome or Playwright Chromium, and the Evergreen WebView2 Runtime for the complete regression suite. Set `JAVA_HOME` to a JDK 17+ installation for the Android Java harnesses.

No Gradle or Maven client is required. The Android wrapper uses platform APIs and the pinned, vendored ZXing core 3.5.3 JAR for local QR decoding; the APK build checks its SHA-256. The bundled web interface stays local. The Windows build restores NAudio.Wasapi 2.2.1 for audio-session control and QRCoder 1.6.0 for local pairing PNG generation. License notices are included in the Windows package and APK assets.

## Clone and publish Windows

```powershell
git clone https://github.com/tugcantopaloglu/tablet-deck-studio.git
Set-Location tablet-deck-studio
dotnet publish .\companion\TabletDeck.csproj -c Release -r win-x64 --self-contained true -o .\dist\Windows
.\dist\Windows\TabletDeck-Companion.exe --self-test
```

Keep the published `wwwroot`, `Sounds`, and `Licenses` folders beside the executable. Twenty-eight self-tests cover defaults, included sound assets, validation, key mapping, OBS authentication, confirmations, Soundpad pipe/XML parsing, actual library HTTP response serialization, app-mixer validation, atomic layout reorder, and Soundpad button import.

The Android build packs bundled assets with the JDK `jar` tool. Windows `aapt2 -A` produced backslashes in asset ZIP entry names during the physical 1.4 test, which Android could not load. Before signing, the build rejects backslash entries and verifies every required UI asset has its exact forward-slash path. See [the hardware test report](LIVE-TEST.md).

An additional real Windows audio smoke test plays and stops a silent WAV, creates its own silent playback session, adjusts only that session's volume/mute, verifies those values, and restores them. It requires a Windows audio endpoint:

```powershell
.\dist\Windows\TabletDeck-Companion.exe --audio-smoke-test
```

Run the companion without `--self-test` to open the editor and tray. Use `--no-browser --no-tray` for server-only tests. Developer options are `--no-browser`, `--data-dir PATH`, `--http-port NUMBER`, and `--https-port NUMBER`. The shipping Android app uses HTTPS port 8743; alternate ports are for isolated development tests.

Version1.11 hosts the editor in a WinForms WebView2 window; the Evergreen Runtime must be installed separately. Microsoft.Web.WebView2 SDK1.0.4314.35 is pinned, with only Core/WinForms referenced and the loader included in publish. Run `scripts/Build-Icons.ps1` after changing assets/app-icon.svg; check in the resulting ICO/PNG/favicon and matching Android vectors. Both Windows projects embed the ICO. To publish while a development companion is running, use an isolated `-p:OutputPath=ABSOLUTE_BUILD_FOLDER` as well as `-o ABSOLUTE_PUBLISH_FOLDER`; the latter alone still builds in bin and can collide with the running executable.

`npm --prefix tests run test:desktop-rendered` starts an isolated service and headless Chromium, then an offscreen native WebView2 test. It covers the timer callback receiver failure, grouped navigation, unsaved navigation, update controls, resizable main/ink windows, their independent close/reopen lifecycle and host policies. It captures content at 1280×860 and 900×620 without contacting providers or modifying the live installation. Set TABLETDECK_COMPANION to the built/published executable and TABLETDECK_CHROME if Chrome is installed elsewhere. The CLI `--desktop-window-self-test --data-dir PATH` requires that runner's marked temporary fixture; it is not a normal launch mode.

`--update-self-test --no-browser --no-tray` runs 26 mocked release/download/ADB cases. `test:update-ui` and `test:updates-api` cover preference/confirmation/permission guards without installing anything. After embedding the release ZIP in the installer, set TABLETDECK_SETUP and run `test:installer` then `test:installer-update`: the latter performs real replacement and forced rollback only inside the fixed, fresh Windows temporary fixture. It never launches a visible app or changes the actual installation. Production `--update-running` is an internal handoff protocol, not a normal installer option to invoke manually.

## Prepare the Android toolchain

Exact official archive URLs and SHA-256 values are in [toolchain-downloads.json](../scripts/toolchain-downloads.json). In PowerShell 7, from the repository root:

```powershell
$tools = Join-Path (Get-Location) 'toolchain'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
$packages = Get-Content .\scripts\toolchain-downloads.json -Raw | ConvertFrom-Json
foreach ($package in $packages) {
    $name = [IO.Path]::GetFileName(([Uri]$package.url).AbsolutePath)
    $archive = Join-Path $tools $name
    Invoke-WebRequest -Uri $package.url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLower() -ne $package.sha256) {
        throw "Checksum mismatch: $name"
    }
    $destination = Join-Path $tools $package.extractTo
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
}
```

Review the publishers' license terms for downloaded SDK tools. The initial archives extract to:

```text
toolchain/
├── jdk/jdk-17.../bin/java.exe
└── sdk/
    ├── android-16.1/aapt2.exe
    ├── android-16.1/zipalign.exe
    ├── android-16.1/lib/
    ├── android-36/android.jar
    └── platform-tools/adb.exe
```

Here `android-16.1` is the build-tools archive's directory name, not the minimum Android version. The scripts also support the normal SDK layout (`platforms/android-36` and `build-tools/36.1.0` or `36.0.0`). Use `-JdkDirectory` and `-AndroidSdkDirectory` to select existing installations.

## Build and sign Android

```powershell
# PowerShell 7, from the repository root:
$tools = Join-Path (Get-Location) 'toolchain'
$output = Join-Path (Get-Location) 'dist'
.\scripts\Build-Android.ps1 -Toolchain $tools -OutputDirectory $output `
  -SigningKey 'C:\Private\tabletdeck.jks' -SigningPasswordFile 'C:\Private\password.txt'
```

The script compiles resources and Java, converts classes to DEX, packages the bundled interface, aligns the APK, signs it, and verifies the result. Output: `dist/TabletDeck-Studio.apk`.

DeskWing retains app identity `app.tabletdeck.studio`, version `1.14.0`, version code `17`, minimum SDK `26`, target SDK `36`. Increment version code for later releases. The build verifies every index script/stylesheet against the native UI_ASSETS allowlist, as well as APK paths and license assets. Desktop-only desktop.html/console.js/console.css are served on PC loopback, not loaded into the privileged Android view. Camera permission remains optional and QR-only.

Release builds require the original signing material; they never generate or replace a release key. Defaults remain `toolchain/signing/tabletdeck.jks` and `toolchain/signing/password.txt`, or supply the explicit paths above. Keep these private. `-ExpectedCertificateSha256` can enforce the expected signer. A replacement key cannot update an existing installation signed with the original key.

For compile checks without a release key, use `-Unsigned`. To install a separate development build alongside the existing app, use `-Development`; it uses package `app.tabletdeck.studio.development` and an isolated development-only key under ignored `.test-output`. It does not replace production pairing or signing material.

## Build an Android App Bundle

The bundle script uses the same source/assets and signing identity as the APK. It validates the AAB structure and, for signed builds, verifies that its generated universal APK matches the source APK signer and bundled content.

```powershell
.\scripts\Build-AndroidBundle.ps1 -Toolchain $tools -OutputDirectory $output `
  -SigningKey 'C:\Private\tabletdeck.jks' -SigningPasswordFile 'C:\Private\password.txt' `
  -DownloadTools
```

`-DownloadTools` obtains the pinned, checksum-verified release tools if missing. `-ReleaseToolsDirectory` selects a prepopulated tool directory. `-Unsigned` and `-Development` have the same distinct purposes as the APK builder. The output `android-bundle.json` records package/version, hashes and signer verification. Building an AAB does not upload it to Google Play; release signing and console submission remain separate steps.

## Assemble the full package

Place the APK directly under `dist` and publish the companion to `dist/Windows`. Copy `USB-Connect.cmd`, `USB-Connect.ps1`, `Install-APK.cmd`, and `Install-APK.ps1` from `scripts` into `dist/Windows`.

Copy `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`, and `NOTICE.txt` from the verified platform-tools archive into `dist/Windows/platform-tools`. Preserve the included font, DOMPurify, icon, NAudio, Android-tool, and Microsoft runtime notices when distributing those components.

Do not bundle runtime configuration, paired-device records, private certificates, or signing material.

## Run the complete regression suite

From the repository root on Windows:

```powershell
npm ci --prefix .\tests
# Set this to your installed JDK if JAVA_HOME is not already configured.
$env:JAVA_HOME = 'C:\Path\To\jdk-17'
npm test --prefix .\tests
```

`npm test` now runs `tests/run-all.cjs`. It checks prerequisites, publishes a fresh companion into a unique `.test-output/verification-*/Windows` directory, and runs an explicit suite manifest serially. It does not reuse the installed companion, start streaming apps, play audio, inject real shortcuts, install a driver, or operate a physical tablet. The companion self-test validates generated WAV content without an audio device; actual playback and audio-session volume/mute checks remain opt-in through `--audio-smoke-test`. Existing environment variable and executable names retain `TABLETDECK` for compatibility with earlier tools.

The manifest includes companion self-tests; API, execution, profile, update, media, readiness, recovery and shared-ink tests; Java transport, QR, chat-policy, request-size and queued-control harnesses; controller fixtures; current rendered desktop/tablet/editor/macro/pen workflows; and offscreen native WebView2 lifecycle checks. Pen service tests use a fake platform and never inject Windows input. Preview tests use generated frames to check lease ownership, capture limits and independence from pen traffic. Shared-ink tests cover revisions, concurrent edits, persistence and request-size boundaries. The original integration runs against its own mock OBS in API-only mode; current rendered suites cover the separated desktop/tablet surfaces. The feature suite opens the actual PC editor and a fixture-only tablet document, exercising real isolated layout endpoints while mocking playback controls.

No live test server needs to be started manually. The runner creates and shuts down the original integration fixture; other suites manage their own isolated services. Fixed ports in existing suites must be free, so do not run two complete runners at the same time. Runtime data lives under a fresh `%TEMP%\deskwing-verification-*` directory so mandatory private-directory ACLs work even if the checkout is on another filesystem. The published executable remains under `.test-output`, as required by the live-scene regression.

The browser is selected from `TABLETDECK_CHROME`, installed Chrome, or the Playwright Chromium cache, in that order. To install the latter, run `npm exec --prefix tests -- playwright install chromium` from the repo or `npx playwright install chromium` inside `tests`. Missing Java, browser, dependency, or suite files are failures, not skipped checks. The native desktop check requires the [Evergreen WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2).

```powershell
# Inspect the manifest without building or launching anything.
npm run test:list --prefix .\tests

# Focused groups still publish an isolated companion by default.
npm run test:core --prefix .\tests
npm run test:android --prefix .\tests
npm run test:ui --prefix .\tests

# Reuse a known isolated build for a focused rerun.
$env:TABLETDECK_COMPANION = 'F:\streamer-board\.test-output\verification-example\Windows\TabletDeck-Companion.exe'
node .\tests\run-all.cjs --no-build --suite 'editor-workflow,macro-editor,pen-ui'
```

The `core` group includes API and controller tests plus mocked backend projects; `android` includes JVM harnesses and the isolated services those harnesses exercise; `ui` includes headless Chromium and the offscreen native window check. The default `all` group includes all three. For custom options, invoke `node tests/run-all.cjs` directly and quote comma-separated suite names so PowerShell's npm shim cannot reinterpret them. Test failures are collected while remaining independent suites continue; the command exits unsuccessfully if any suite fails. Prerequisite or publish failures stop immediately. Use `--fail-fast` for a focused debugging run. Results and unrun suites are recorded in `.test-output/verification-*/summary.json`; logs remain beside the summary. A partial or focused result is not a complete release pass.

Installer tests are an explicit opt-in because they need a packaged installer and perform actual replacement/rollback inside their guarded temporary fixture:

```powershell
$env:TABLETDECK_SETUP = 'C:\Path\To\TabletDeck-Setup.exe'
# Only needed when testing an artifact with a different version from the checkout.
$env:TABLETDECK_VERSION = '1.14.0'
npm run test:installers --prefix .\tests
```

Benchmarks, `--audio-smoke-test`, media `--probe`, native pen input into a controlled receiver, APK installation and physical USB/Wi-Fi/pen rehearsals remain separate. Passing the full regression does not certify Xiaomi palm rejection, handwriting recognition, Windows application compatibility, audio routing, endurance, store acceptance or real second-display performance.

`.github/workflows/verify.yml` runs the same complete command on Windows with .NET 10, Node 22, JDK 17, Chromium and WebView2. A separate macOS 26 job generates the iOS project and runs its unsigned iPad simulator tests with Xcode 26 or newer. The iOS job requires no Apple account; it is a compile/test gate, not a signed device build or App Store submission. See [the iOS host guide](../ios/README.md) for local Mac and signing steps. The Windows command never skips platform tests and calls them passed: iOS is a distinct CI job and still requires its own successful result.

Actions are pinned to commit IDs. CI uploads only the Windows summary, never runtime fixtures: those contain private test credentials and certificate keys. Do not publish the private `%TEMP%` fixtures or the entire `.test-output` tree.

## Security checks

```powershell
npx snyk code test .
npm audit --prefix .\tests
dotnet list .\companion\TabletDeck.csproj package --vulnerable --include-transitive
```

Snyk authentication may be required. `.dcignore` excludes generated builds and separately audited third-party code. The initial scan retains the informational `java/JavaScriptEnabled` note for the bundled Android interface. That does not justify loading arbitrary remote pages into its native bridge.

## Architecture

Android bundled UI → restricted Java bridge → pinned persistent TLS control (8744) or pinned HTTPS fallback/metadata (8743) → Windows companion → authenticated OBS v5 or a configured Windows action. PC panels receive authenticated state events.

USB adds ADB reverse tunnels for ports 8743 and 8744. The PC editor uses a separate authenticated loopback endpoint. Tablet action requests contain configured action IDs; PC targets are edited locally. Paired tablets can also reorder the exact existing button set, play library indices validated against Soundpad, and adjust enumerated audio sessions by opaque IDs. Soundpad button import remains local-admin only. Preserve these boundaries when adding features.

## Build the Windows installer

Assemble the complete release ZIP with SHA256SUMS.txt listing every file except itself, then embed it:

```powershell
$payload = (Resolve-Path .\dist\DeskWing-1.14.0.zip).Path
dotnet publish .\installer\TabletDeck.Setup.csproj -c Release -o .\dist\Setup "-p:PayloadArchive=$payload"
.\dist\Setup\TabletDeck-Setup.exe --verify-payload
```

The installer uses a fixed per-user destination. `--test-mode --quiet` installs only to the fixed temporary `TabletDeckStudioSetupTests/TabletDeckStudio` folder, without shortcuts or registry changes. `--test-mode --uninstall` removes its managed test files. It refuses an existing unrecognized folder. Keep test mode away from any user-created folder with that name. Payload verification does not install or launch the app.

## Test without opening apps

```powershell
.\dist\Windows\TabletDeck-Companion.exe --self-test
.\dist\Windows\TabletDeck-Companion.exe --hardening-self-test
.\dist\Windows\TabletDeck-Companion.exe --boards-self-test
.\dist\Windows\TabletDeck-Companion.exe --studio-self-test --no-browser --no-tray
.\dist\Windows\TabletDeck-Companion.exe --monitor-self-test --no-browser --no-tray
.\dist\Windows\TabletDeck-Companion.exe --live-soundpad-self-test
$env:TABLETDECK_COMPANION = (Resolve-Path .\dist\Windows\TabletDeck-Companion.exe).Path
npm run test:headless --prefix .\tests
npm run test:fast --prefix .\tests
npm run test:execution --prefix .\tests
npm run test:boards --prefix .\tests
npm run test:desktop --prefix .\tests
npm run test:studio --prefix .\tests
npm run test:studio-ui --prefix .\tests
npm run test:monitor --prefix .\tests
npm run test:monitor-ui --prefix .\tests
npm run test:live-soundpad-board --prefix .\tests
npm run test:media-sessions --prefix .\tests
npm run test:media-touchbar-ui --prefix .\tests
npm run test:integration-headless --prefix .\tests
```

`test:integration-headless` remains a focused compatibility entry point: it starts and stops its own isolated companion on ports 18740/18743 with a synthetic OBS service on 19455 and skips browser launch. The complete runner uses dynamically allocated companion ports for the same API integration. Default `npm test` includes headless browser and offscreen native-window tests; use `test:core` and `test:android` when rendered execution is not desired.

The headless, fast, execution, and boards suites accept `TABLETDECK_TEST_OUTPUT` as a parent directory for isolated test data. Use a local Windows temporary folder if your checkout cannot apply the private data directory ACL. The desktop suite defaults to temporary storage. Never point fixtures at your real `%LOCALAPPDATA%\TabletDeck` directory. `JAVA_HOME` selects the JDK for the fast-link tests.

The safety tests use fake keyboard sinks and HTTP fixtures. They do not inject keys, write startup settings, open streaming apps, or control Voicemeeter. JavaScript logic tests run without a browser and do not verify layout or native Android touch behavior.

The platform-only Java fast-link test needs JDK17 via JAVA_HOME or the documented toolchain. See [performance measurements and benchmark commands](PERFORMANCE.md). Alternate --https-port also shifts the default fast port to the next integer; --control-port overrides it. Android release clients use the shipping 8743/8744 ports.
