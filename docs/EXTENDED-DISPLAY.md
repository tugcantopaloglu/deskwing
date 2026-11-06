# Optional independent Windows desktop

Computer Copilot's contextual controls work without a display driver. The optional Second display feature creates another Windows monitor and sends **only that monitor** to the paired tablet. It needs a compatible virtual display driver already installed on Windows. Replaying a physical monitor's screenshot does not create another desktop.

This guide describes the **1.15.0 development source**, including optional native Pen/Touch input, picture presets, adaptive quality and hardware-encoder fallback. The [1.14 hardware checkpoint](#114-hardware-checkpoint--2026-11-06) below records earlier measured viewing and lifecycle results; it does not validate those new 1.15 paths or a 1.15 device upgrade. Google Play internal testing is the current distribution priority; [beta preparation](BETA-RELEASE.md) separates build, signing, device and store gates.

The current integration targets the installed SudoVDA 0.2 device protocol. DeskWing does not download or install a driver, import a certificate, request elevation, change boot signing settings, or change the arrangement of existing physical monitors. Retained installation/data identifiers are explained in the [upgrade compatibility note](../README.md#1150-development).

## Use the installed driver

1. Open Computer Copilot on the PC and inspect Second display's installed-driver publisher and trust information.
2. Choose 1280×800, 1600×900, or 1920×1080 and the Windows display refresh rate. The UI offers 60, 90, 120, 144, 165, or 240 Hz; 60 Hz is the default. Explicitly accept the installed driver and sharing permission, then choose **Create independent display**. Creation is restricted to the authenticated local PC console and remains subject to installed-driver/Windows support.
3. Windows should expose the new DeskWing monitor. If its saved configuration leaves it disconnected or duplicated, use Windows **Settings → System → Display → Extend** for that monitor. DeskWing will report the actual topology and refuse capture until it has a separate, nonoverlapping desktop source.
4. If needed, choose **Allow tablet sharing** on the PC. Move the desired windows onto the extra monitor using normal Windows controls. On the tablet choose **Refresh rate → 60 Hz** or **Match tablet**, a picture preset and adaptive-quality setting, then **Start second display** for native H.264 viewing. Defaults are 60 Hz, Balanced and adaptive quality enabled. The native viewer starts in **View** mode. **Open compatibility preview** is the separate JPEG option.
5. Choose **Exit display** when finished viewing. **Pause tablet sharing** revokes viewing; **Remove extra display** removes this session's monitor and lets Windows move its windows to the remaining displays. Exiting the viewer alone leaves the Windows monitor intact.

The app never selects an existing physical desktop as a fallback. Starting another SudoVDA session while one belongs to another application is refused. Each DeskWing session uses a random monitor GUID and removes only that GUID. Nothing changes physical monitor resolutions, positions, or the primary display.

The native path uses a persistent authenticated H.264 stream and a native tablet decoder. Match tablet chooses the tablet's reported current refresh rate, not its maximum. The actual Windows source must run at least as fast as the request; a 60 Hz source is rejected for a 120 Hz request. Stop viewing and recreate the extra display at a sufficient rate if needed. Android checks hardware decoding support for the chosen resolution/rate. Browser viewing uses the explicit compatibility option. A failed native start does not silently choose another transport.

**Open compatibility preview** remains bounded JPEG: up to four captures per second, widths of 640/960/1280 pixels, and 384 KiB per JPEG. The current UI requests 960 pixels. Busy requests are dropped instead of queued. A higher Windows monitor refresh does not increase this fallback's transport rate. It suits relatively static panels and references.

Both video paths are silent. The JPEG compatibility preview is view-only; the native viewer can send pen input after the separate permission below. Protected content may appear black. **Pen tablet** remains a different workspace, with its own selected-window/screen JPEG preview and explicit Windows input session.

## Pen and single-finger Touch

Viewing permission does not grant control. On the PC, explicitly enable and confirm **Allow my paired tablet to tap and draw on this extra display** for the current owned monitor. The permission starts off, stays in memory, and is bound to that monitor's identity and generation. It is not saved in the user's board or reused for a different display.

After starting native viewing, choose **Pen** or **Touch** in the tablet overlay:

- **Pen** accepts the stylus or Apple Pencil and ignores finger contacts. It forwards pressure and tilt when the device exposes them. Android also forwards reported hover, barrel-button and eraser data; those Android capabilities are not promises about every pen or the iOS path.
- **Touch** turns one finger into synthetic Windows pen taps and drags with fixed pressure and zero tilt. It does not provide multitouch, pinch zoom, two-finger scrolling or Windows touch injection. A second eligible contact cancels the stroke until the contacts lift.
- **View** stops forwarding input while keeping the picture visible.

Input is restricted to the active native stream's exact extra monitor, with current geometry and a renewable input lease. Ending video, revoking input/sharing/pairing, losing the source or connection, lease expiry and shutdown release the active pen contact. Restarting input is explicit. App support still depends on the selected Windows application's ink settings and brush; rehearse the actual app before relying on pressure, tilt or other pen behavior. These 1.15 controls need their own physical mapping, palm and lifecycle checks; the older 1.14 viewing results below do not cover them.

## Picture quality and encoding

The tablet offers **Balanced**, **Sharper picture**, and **Save power and bandwidth**. These presets set initial and minimum video bitrates while preserving source dimensions and the requested refresh target. Their names describe intent, not measured battery savings or guaranteed frame delivery.

**Adjust quality when the connection is busy** is enabled by default. It responds to measured capture/encoding and stream-write time by lowering bitrate and capture scheduling, then gradually recovering. The scheduling target has a 60 fps floor; that floor is not proof that the complete pipeline achieves 60 fps. Runtime bitrate changes depend on encoder support. Adaptive quality does not resize the source or fabricate duplicate frames to raise counters.

Windows automatically tries a compatible hardware H.264 encoder and falls back to software when the hardware path is unavailable or incompatible. There is no user-facing hardware/software switch. A failure before the first published picture can use software fallback; a failure after publication ends the session and requires an explicit restart. A failed encoder/format combination has a bounded five-minute cooldown. Hardware encoding is not assumed to be faster on every PC.

PC stream status identifies the encoder, hardware/software path, bitrate, fallback reason when present and adaptive state. The native overlay's frame counters describe reception/rendering or screen cadence, as detailed below. They are not an encoder benchmark.

## Refresh targets and observed delivery

The UI keeps **Windows display refresh rate**, tablet current/max refresh, and video counters separate. During the 1.14 rehearsal, the user's tablet reported 120 Hz current and 165 Hz maximum through ADB; Match tablet therefore requested 120 Hz at that time. A capability observation alone does not demonstrate 120 fps video delivery.

Android's viewer reports **fps shown** from decoder rendering callbacks, **fps received**, target Hz, and current tablet Hz. It marks delivery below target. iOS reports received fps and measured screen cadence; these are not displayed-video-frame measurements. When content is unchanged, the last picture stays visible with **Still image** status. Keepalive heartbeats do not count as video pictures. Video preserves source proportions with unused edges, and Exit display stays visible.

The Windows producer prefers DXGI GPU duplication of the exact owned display output, including its cursor. A persistent GDI fallback is permitted only at 60 Hz; higher requests require supported DXGI capture. Capture and encoding are separate choices: DXGI capture does not prove hardware encoding. Source capture, encoding, network throughput, decoder support, resolution, power state, and display scheduling all affect delivery. Use moving content and the actual viewer counters during physical rehearsal; configured 60/120 Hz is not an end-to-end performance result.

## Diagnose without changing anything

From the repository root, with Windows PowerShell 5.1 or PowerShell 7:

```powershell
.\scripts\Virtual-Display-Probe.ps1 -Json
```

The helper reads `QueryDisplayConfig`'s active paths and Windows' installed adapter inventory. Shared source identities indicate cloned output; distinct source identities indicate independent desktop surfaces. Multiple physical monitors alone do not verify a tablet display. `tabletExtendedDisplayVerified` deliberately remains false: this read-only probe cannot verify tablet delivery.

The companion's status adds installed SudoVDA interface discovery, opening its interface with the current user's permissions, and Windows trust verification of the installed catalog's membership for both its INF and DLL. These checks do not send a driver IOCTL, create a monitor, or read desktop pixels. Protocol compatibility is checked only during an explicit create operation. A local trusted catalog is not a WHQL certification statement.

If interface access fails, status includes the actual Windows error, including access denied. DeskWing does not elevate or modify device permissions. Driver installation or repair belongs to the driver's publisher/admin workflow. Microsoft's [PnPUtil documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil) describes its administrator requirement. Adding an INF package alone may not create a root-enumerated virtual device; use the publisher's supported setup rather than assuming `pnputil /add-driver` is sufficient.

## Development contract

`companion/ExtendedDisplay.cs` owns the driver handle, session identity, 400 ms watchdog, topology checks, sharing state, and bounded capture. Its constructor is inert; `Dispose()` attempts to remove the session's own monitor and closes resources. If an explicit Stop fails, sharing switches off and ownership is retained for a retry; a failed removal is not reported as stopped. No generated protocol constants or device paths are accepted from a network client.

| Route | Access | Request / result |
| --- | --- | --- |
| `GET /api/computer/display` | Paired tablet or local PC admin | Actual status; no pixels |
| `POST /api/admin/computer/display` | Local PC admin | `operation: "create"`, `width`, `height`, `refreshRate` (default 60, allowed 60–240), `acceptInstalledDriver: true`, optional `shareWithTablet` (default false) |
| Same admin route | Local PC admin | `operation: "share"`, `enabled: true/false` |
| Same admin route | Local PC admin | `operation: "remove"` |
| `POST /api/computer/display/frame` | Paired tablet or local PC admin, explicit PC sharing also required | `{ "width": 960 }`; JPEG data URI plus dimensions, sequence and timestamp |
| `GET /api/computer/display/stream/status` | Paired tablet or local PC admin | Current video status, encoder and adaptive-quality telemetry |
| `POST /api/computer/display/stream/start` | Authenticated client; owned independent display and sharing required | `{ "refreshRate": 120, "qualityPreset": "balanced", "adaptiveQuality": true }`; owner-bound lease, source dimensions, requested rate, actual source rate, and `codec: "h264"` |
| `POST /api/computer/display/stream` | Same authenticated owner and unused live lease | `{ "lease": "…" }`; binary `application/vnd.deskwing.h264` response, with caching disabled |
| `POST /api/computer/display/stream/stop` | Same authenticated owner and lease | `{ "lease": "…" }`; ends only that matching stream |
| `GET /api/computer/display/input` | Paired tablet or local PC admin | Current input permission and session status |
| `GET /api/admin/computer/display/input` | Local PC admin | Input status for the current owned monitor |
| `POST /api/admin/computer/display/input` | Local PC admin | Explicit permission change with `enabled` and current `targetIdentity` |
| `POST /api/computer/display/input` | Authenticated native video owner; separate PC input permission required | Bounded begin/samples/end/cancel requests; validated owner, input lease, sequence and geometry |

Status fields include `available`, `active`, `sharedWithTablet`, `independentDesktop`, `sourceName`, `width`, `height`, actual `refreshRate`, `driver`, `detail`, and `displays`. `available` identifies an accessible, locally trusted candidate package; it does not prove a monitor was created. `active` means this process owns a driver session; `independentDesktop` requires Windows to expose that session's unique, nonoverlapping source. Frames recheck the monitor identity and bounds before and after capture. Clone, overlap, missing targets, unshared sessions, and topology changes fail closed.

`DisplayStream` runs one owner-bound native stream and one capture/encoder worker at a time, separate from control and pen requests. A start lease must attach within 5 seconds. Source identity, generation, geometry, actual refresh, sharing, pairing, and shutdown state are validated before publishing encoded data. Each H.264 access unit is bounded to 2 MiB; writes have a 2-second timeout and backpressure rather than an accumulating frame queue. A non-cooperative write retains the sole worker slot until it settles. Source refresh must meet the request, allowing 0.1 Hz tolerance for nominal rates such as 59.94 Hz.

`DisplayInput` binds its separate 1.2-second renewable input lease to the active video owner, video lease and exact owned monitor geometry. It validates bounded samples and contact transitions, rejects arbitrary monitor/window targets, and releases injected pen state on invalidation. Video sharing and input permission are independent gates; neither changes board configuration.

An unchanged DXGI output sends a zero-payload heartbeat with flags 8 at 500 ms intervals, retaining the last picture without recapturing/encoding static content or inflating received/rendered video counts. The first fresh picture after an idle gap of at least 500 ms forces an IDR so the native decoder can resume from a complete picture.

Native apps use `/local/display/capabilities`, `/local/display/start`, and `/local/display/stop` handlers with their existing pinned PC connection. Starting requires the trusted page and foreground host; stop remains available for lifecycle cleanup. Video travels over pinned HTTPS, independently of the fast control link. Background/exit, connection failure, revoked sharing/pairing, source topology/rate changes, or decoder failure end viewing and clear the session. Restart is explicit. The app does not save streamed frames to disk.

The SudoVDA watchdog is global to that driver. DeskWing refuses to start when that adapter already has active targets from another application. Its heartbeat runs only while its own session is alive. Do not run competing virtual-monitor managers against this same driver concurrently.

The original interop follows the [SudoVDA protocol header at commit a4b09fa](https://github.com/SudoMaker/SudoVDA/blob/a4b09fa2aa731a964d0cb5d139cb1e6240e4da12/Common/Include/sudovda-ioctl.h); no driver code or binaries are bundled. The [upstream README/license statement](https://github.com/SudoMaker/SudoVDA/tree/a4b09fa2aa731a964d0cb5d139cb1e6240e4da12) identifies MIT/CC0/public-domain terms for its changes and refers to Microsoft's terms for Microsoft's sample. A future redistributed driver must retain the applicable notices and receive its own distribution review.

Microsoft describes [IddCx's UMDF architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview), [active desktop topology queries](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig), and [driver catalog package signatures](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/catalog-files). Building a new driver requires the appropriate WDK and signing/release process; compiling a sample is not a signed production-driver installation.

## Inspection and verification on 2026-11-01

Read-only inspection of the development PC found SudoMaker Virtual Display Adapter **1.10.9.289**, instance `ROOT\DISPLAY\0000`, INF `oem107.inf`. Its catalog and its INF/DLL membership verify against this PC's trust store, publisher `CN=sudovda@su.mk`, certificate thumbprint `3C918FC73525AD8B1521B6DB26B71F694277CC49`. CIM's `Win32_PnPSignedDriver.IsSigned` reported false; this difference is retained as evidence and is not presented as Microsoft/WHQL certification.

| Installed file | SHA-256 observed locally |
| --- | --- |
| `SudoVDA.dll` | `47EE263CB5DE9382C6630A2D7F3DAFEC4A49419F953BEEC869CA5DD0C460FF63` |
| `sudovda.cat` | `2F9189DE5604BEC9D86F51640CC540639E394D9AD0F8E689129375E95F2D22F8` |
| `SudoVDA.inf` | `AD69AC682756F0CF339B081FAC7E6E8159FDF2CA01CA69DF8945C7246C286925` |

These identify the installed files, not a verified downloadable release. The [VirtualDrivers release page](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases) and its GitHub release API exposed only prereleases `24.10.27` and `24.10.16-beta` during inspection, despite newer-version descriptions elsewhere. Tag `24.10.27` resolves to `15c71366510faf19e327671b1a07d69f6fd0c2e2`; its x64 ZIP's release metadata had no digest. No current, independently pinned Microsoft-signed downloadable package was established, so no automatic installer or recommended download hash is provided.

The active topology contained two physical desktops (R27qe 2560×1440 and XV272 S 1920×1080), and **no active virtual desktop**. SDKs 22621/26100 and SignTool were present, but the inspected Windows Kits had no IddCx header/WDF build tree. No driver build/install, monitor creation, boot configuration change, real pixel capture, or tablet delivery was performed during development verification.

The new component built with zero warnings/errors. `ExtendedDisplayTests.Run()` passed 31 deterministic checks with a fake driver: fixed protocol structures, acceptance/resolution/trust/protocol guards, cancellation before writes, foreign-monitor refusal, own-GUID cleanup, malformed-response cleanup, failed-removal retry, sharing revocation, and clone/overlap capture refusal. A separate real read-only run verified accessible interface discovery and installed package trust, retained the two physical displays, and rejected capture while sharing was off. Real creation, watchdog behavior, Windows activation, JPEG capture, and physical tablet delivery still require the explicit PC workflow above.

## High-refresh verification status

The preceding section is the historical 2026-11-01 baseline. A later isolated test used an animated fixture on an owned 165 Hz monitor and measured warmed PC capture/encode rates of 60.01 fps at a 60 Hz target and 118.75 fps at 120 Hz. It removed its own monitor afterward and left physical monitor topology unchanged. These PC-side measurements excluded network transport and tablet playback; an encoder-only benchmark excludes monitor capture as well. The separate physical device results and final metrics verification are recorded below. They do not establish end-to-end latency or exhaustive interruption/load reliability.

## 1.14 hardware checkpoint — 2026-11-06

All measurements and checks in this section belong to **1.14**, before the 1.15 native input, notebook/PDF, adaptive-quality, hardware-encoder and public-artifact rename changes. They do not certify a 1.15 installation or store package.

Candidate `002c3d5` passed all 65 Windows suites locally and in hosted CI, plus 39 iOS simulator tests. The subsequent metrics-only commit `9989c86` passed 78 focused metric checks, complete SDK 36 / Java 8 compilation, and [final CI](https://github.com/tugcantopaloglu/deskwing/actions/runs/42252632169) with 65 Windows suites and 39 iOS tests. Both [actual Snyk checkpoints](SECURITY-DISPLAY-RESUME-2026-11-06.md) retained seven existing Low findings with no new issues. Original-signer APK/AAB/universal packages were verified. The final universal APK installed at that checkpoint had SHA-256 `9bef037cf14fb60aec8b693967e05976cfd33fdaba8c2c1d9509ce8ef35ed850`. Android opened without the historical UI-thread TLS-cleanup crash.

The user confirmed that the 60 Hz desktop appears and motion is smooth. During bounded moving-cursor checks, Android received **59.87–59.88 fps**, with rendered counts keeping pace and zero reported drops. The user also confirmed smooth Match tablet viewing with screen and target both at 120 Hz: samples measured **118.72–119.80 fps received**, with received/rendered counts of 232/231 and later 470/470 and zero drops. Static content correctly entered Still image without artificial frames. Exiting 60 Hz and explicitly starting Match tablet worked without a crash. These observations do not establish end-to-end latency or guarantee every resolution/content workload.

The earlier `render_estimate_fps` remained zero despite increasing render counts because freshness compared different clock origins. Commit `9989c86` uses the callback's local observation clock for freshness while retaining render-time intervals for cadence. After installation, the user confirmed “Works, and FPS counter rises” for Match tablet motion and Home/reopen/start. Final samples reported **120.10 fps as the decoder-rendered estimate**, alongside **120.69 and 119.83 fps received**, with zero reported drops. The new 60 Hz session after Home/reopen advanced render counts, the app stayed alive, and no AndroidRuntime error was recorded. The shown rate is a decoder callback estimate, not camera-measured panel cadence.

Sharing was disabled while video was live: all three DeskWing SurfaceView layers disappeared, the app stayed alive, and no AndroidRuntime error was recorded. The owned `DISPLAY5` was then removed; both physical monitors' names, positions, and sizes were exactly unchanged. A new owned **1280×800, 165 Hz** display was recreated with sharing enabled and intentionally left active for continued normal use at that checkpoint. Removal/topology cleanup was verified. The 1.15 input and quality paths need fresh device evidence; exhaustive USB/Wi-Fi interruption, latency and load cases, physical iPad/Pencil behavior, and store submission remain separate checks.
