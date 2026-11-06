# Computer Copilot controller foundation

Computer mode provides a contextual touch controller for Windows applications. Its primary job is a tablet touch bar with media, app navigation, editing, and timeline controls. It is opt in and starts disabled. It does not open applications, inspect chat contents, submit prompts, install a display driver, capture a screen, or create an extended desktop.

The production backend is `companion/ComputerCopilot.cs`. The host supplies the existing guarded `DeckEngine.Execute` path as `Func<DeckAction, Task>`; this component never calls keyboard injection directly. The controller resolves saved preset/control IDs into one `hotkey` action using the existing `WindowsActions.ParseHotkey` allowlist. There are no hold leases, text injection, scripts, commands, or macros in this protocol.

## Host integration

Create one `ComputerCopilot(store.DirectoryPath, guardedExecute)` for the host lifetime. `guardedExecute` must acquire the same `FastCommands` configuration lock used by ordinary actions, reject maintenance, held keys and conflicting editing/action activity as applicable, and create the `engine.Execute(action)` task while holding that lock. Await the task after releasing the host lock. The controller never holds its own settings lock while entering this delegate.

Keep existing authentication, origin checks, maximum body size, and error handling for all routes. Expose the following routes explicitly in both Android and desktop native proxy allowlists where needed; do not broaden either allowlist to arbitrary `/api/*` routes.

| Route | Access | Request | Result |
| --- | --- | --- | --- |
| `GET /api/computer` | Paired tablet or PC admin | None | `computer.Snapshot()` |
| `POST /api/computer/preset` | Paired tablet or PC admin | `{presetId}` (`CopilotPresetRequest`) | Call `SelectPreset`; return `{ok:true}` |
| `POST /api/computer/action` | Paired tablet or PC admin | `{presetId,controlId,contextToken}` (`CopilotActionRequest`) | `await computer.Execute(request)` => `{ok:true}` |
| `GET /api/admin/computer` | PC admin only | None | `computer.Settings` directly |
| `POST /api/admin/computer` | PC admin only | Complete `CopilotSettings` object | Call `Save`; return `Snapshot()` or `{ok:true}` consistently with frontend adapter |

No separate fast socket command is required: first implementation may use the already authenticated HTTP proxy for these routes. If added to the persistent control protocol, preserve identical validation and guarded execution.

## State and editable metadata

```json
{
  "settings": {
    "enabled": false,
    "automatic": true,
    "manualPresetId": "desktop",
    "presets": [{
      "id": "resolve",
      "label": "DaVinci Resolve",
      "applications": ["Resolve.exe"],
      "controls": [{
        "id": "frame-forward",
        "label": "Next frame",
        "group": "Timeline",
        "shortcut": "RIGHT",
        "global": false
      }]
    }]
  },
  "foreground": {"application":"Resolve.exe","available":true},
  "detectedPresetId": "resolve",
  "activePresetId": "resolve",
  "contextToken": "opaque 32-character lowercase hexadecimal token",
  "status": "Controls follow the foreground app.",
  "capabilities": {
    "contextualControls": true,
    "displayCapture": false,
    "extendedDisplay": false,
    "displayNote": "A real extra Windows desktop requires a separately installed virtual or indirect display driver and tablet transport. This controller does not install a driver or create a display."
  }
}
```

All preset labels, executable associations, control labels, groups and shortcuts are PC editable. Settings are atomically persisted under the companion's existing protected data directory as `computer-copilot.json`. Invalid/damaged settings fail closed to disabled defaults with a visible recovery message.

Automatic selection uses only the foreground process executable name. It never reads window titles, browser tabs, URLs, clipboard contents or document text. Exactly one matching preset selects automatically; zero or multiple matches retain the saved manual preset. YouTube and Claude both list browser executables, so browser content is intentionally a manual choice. Selecting a preset pins its layout by setting `automatic=false`; it neither opens nor focuses the target application. Enable automatic selection again in the PC settings.

Every action includes the latest `contextToken`. Settings changes, preset changes, process changes and foreground-window changes invalidate it. Non-global controls additionally require the foreground executable to match the selected preset. Global controls support only Windows media keys, `ALT+TAB`, and `WIN+TAB`. The foreground check precedes dispatch, but Windows input remains dependent on focus at the actual instant of injection; do not claim this is an application-specific automation API. Windows can block input across integrity levels.

AI assistant panel commands are empty assignment placeholders because Codex, Claude and editor extension shortcuts vary by application/version/keymap. VS Code-specific controls are explicitly labelled as such. Empty bindings cannot execute. Enter-containing chords are rejected so these controls cannot use Enter shortcuts to submit chat messages. No preset includes a default Send action.

Limits: 1–16 presets, 1–40 controls per preset, 0–20 unique executable names per preset, and a 128 KiB settings file. IDs use 1–64 ASCII letters/digits/underscores/hyphens. Labels and groups are nonblank, at most 80 characters with no control characters. Executable names are at most 100 characters, end in `.exe`, and contain only ASCII letters/digits/spaces/dots/underscores/hyphens. Shortcut strings are at most 80 characters; empty means unassigned. Supported keys are the existing WindowsActions shortcut grammar: letters, digits, F1–F24, CTRL/CONTROL, SHIFT, ALT, WIN, TAB, ESC, SPACE, BACKSPACE, DELETE, arrow keys, PLAYPAUSE, NEXTTRACK, PREVTRACK, VOLUMEUP, VOLUMEDOWN, VOLUMEMUTE. Repeated keys and modifier-only shortcuts are rejected.

## Extended display boundary

Screen mirroring sends pixels from an existing display. A genuine additional Windows desktop must first exist in Windows' display topology through a supported indirect/virtual display driver plus a frame transport and tablet presentation path. These are distinct capabilities. The shortcut-controller backend's own flags remain `extendedDisplay=false` and `displayCapture=false`; the separate [Second display service](EXTENDED-DISPLAY.md) reports its actual driver, owned-display and viewing state. Opening contextual controls does not create a monitor or install a driver. Native viewing and explicitly enabled pen input have their own permissions and leases.

## Verification

Run `dotnet run --project tests/copilot-backend/CopilotBackendTests.csproj --configuration Release`. The suite links the production controller and shortcut parser with minimal host data contracts. Its foreground reader and executor are fakes. The checks cover stale-window/settings tokens, mismatched focus, browser ambiguity, custom saved bindings, global media allowlisting, concurrent taps, persistence, corrupt settings, unsupported keys, no chat Enter shortcut, opt-in behavior, and truthful display capabilities. Tests never inject keys, open apps/windows, contact real OBS, or read/write user settings.

For a full source compile while the live companion executable is running, use an isolated output directory, for example `dotnet build companion/DeskWing.csproj --configuration Release --no-restore -o .test-output/copilot-backend-build`. Do not stop or overwrite the live executable for a compile check.

Required Snyk review applies to first-party changes. Run the actual `snyk_code_scan`, resolve new findings and rescan the final source before distribution; record the exact candidate and result in its security report. A successful local compile and these isolated tests are not a replacement for that scan.

## Primary references

- [YouTube's player shortcuts](https://support.google.com/youtube/answer/7631406?hl=en): K/J/L, mute and full screen defaults. Focus and player state still matter.
- [VS Code default keybindings](https://code.visualstudio.com/docs/reference/default-keybindings) and [keyboard shortcut configuration](https://code.visualstudio.com/docs/configure/keybindings): command palette and quick-open controls; user keymaps can differ.
- [Blackmagic Design DaVinci Resolve Editor's Guide](https://documents.blackmagicdesign.com/UserManuals/DaVinciResolveEditorsGuide.pdf): J/K/L transport and arrow-key timeline stepping; custom keyboard maps can differ.
- [Adobe's Photoshop shortcut reference](https://helpx.adobe.com/content/dam/help/en/photoshop/using/default-keyboard-shortcuts/photoshop-keyboard-shortcuts.pdf) and [current shortcut settings](https://helpx.adobe.com/uk/photoshop/desktop/get-started/settings-and-preferences/view-keyboard-shortcuts.html): tool/editing defaults and local shortcut customization.
- [Microsoft SendInput](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput): existing injection path, integrity restrictions and focus limitations.
- [Microsoft indirect display driver model](https://learn.microsoft.com/windows-hardware/drivers/display/indirect-display-driver-model-overview): genuine virtual-monitor integration is a driver capability, separate from desktop capture.
