# Optional independent Windows desktop

Computer Copilot's contextual controls work without a display driver. The optional Second display feature creates another Windows monitor and sends **only that monitor** to the paired tablet. It needs a compatible virtual display driver already installed on Windows. Replaying a physical monitor's screenshot does not create another desktop.

The current integration targets the installed SudoVDA 0.2 device protocol. TabletDeck does not download or install a driver, import a certificate, request elevation, change boot signing settings, or change the arrangement of existing physical monitors.

## Use the installed driver

1. Open Computer Copilot on the PC and inspect Second display's installed-driver publisher and trust information.
2. Choose 1280×800, 1600×900, or 1920×1080 and the Windows display refresh rate. The UI offers 60, 90, 120, 144, 165, or 240 Hz; 60 Hz is the default. Explicitly accept the installed driver and sharing permission, then choose **Create independent display**. Creation is restricted to the authenticated local PC console and remains subject to installed-driver/Windows support.
3. Windows should expose the new TabletDeck monitor. If its saved configuration leaves it disconnected or duplicated, use Windows **Settings → System → Display → Extend** for that monitor. TabletDeck will report the actual topology and refuse capture until it has a separate, nonoverlapping desktop source.
4. If needed, choose **Allow tablet sharing** on the PC. Move the desired windows onto the extra monitor using normal Windows controls. On the tablet choose **Refresh rate → 60 Hz** or **Match tablet**, then **Start second display** for native H.264 viewing. **Open compatibility preview** is the separate JPEG option.
5. Choose **Exit display** when finished viewing. **Pause tablet sharing** revokes viewing; **Remove extra display** removes this session's monitor and lets Windows move its windows to the remaining displays. Exiting the viewer alone leaves the Windows monitor intact.

The app never selects an existing physical desktop as a fallback. Starting another SudoVDA session while one belongs to another application is refused. Each TabletDeck session uses a random monitor GUID and removes only that GUID. Nothing changes physical monitor resolutions, positions, or the primary display.

The native path uses a persistent authenticated H.264 stream and a native tablet decoder. Match tablet chooses the tablet's reported current refresh rate, not its maximum. The actual Windows source must run at least as fast as the request; a 60 Hz source is rejected for a 120 Hz request. Stop viewing and recreate the extra display at a sufficient rate if needed. Android checks hardware decoding support for the chosen resolution/rate. Browser viewing uses the explicit compatibility option. A failed native start does not silently choose another transport.

**Open compatibility preview** remains bounded JPEG: up to four captures per second, widths of 640/960/1280 pixels, and 384 KiB per JPEG. The current UI requests 960 pixels. Busy requests are dropped instead of queued. A higher Windows monitor refresh does not increase this fallback's transport rate. It suits relatively static panels and references.

Both paths are silent and supply no touch, pen, or pointer forwarding. Protected content may appear black. Pen tablet is a different workspace, with its own selected-target preview and explicit Windows input session.

## Refresh targets and observed delivery

The UI keeps **Windows display refresh rate**, tablet current/max refresh, and video counters separate. The user's tablet reported 120 Hz current and 165 Hz maximum through ADB; that capability observation means Match tablet requests 120 Hz while the reported current rate remains 120. It does not demonstrate 120 fps video delivery.

Android's viewer reports **fps shown** from decoder rendering callbacks, **fps received**, target Hz, and current tablet Hz. It marks delivery below target. iOS reports received fps and measured screen cadence; these are not displayed-video-frame measurements. Video preserves source proportions with unused edges, and Exit display stays visible.

The Windows producer uses GDI capture of the owned monitor and the built-in software Media Foundation H.264 encoder. It does not currently select GPU encoding. Source capture, encoding, network throughput, decoder support, resolution, power state, and display scheduling all affect delivery. Configured 60/120 Hz and encoder-only synthetic-frame timings do not establish end-to-end 60+ fps or latency. Use moving content and the actual viewer counters during physical rehearsal.

## Diagnose without changing anything

From the repository root, with Windows PowerShell 5.1 or PowerShell 7:

```powershell
.\scripts\Virtual-Display-Probe.ps1 -Json
```

The helper reads `QueryDisplayConfig`'s active paths and Windows' installed adapter inventory. Shared source identities indicate cloned output; distinct source identities indicate independent desktop surfaces. Multiple physical monitors alone do not verify a tablet display. `tabletExtendedDisplayVerified` deliberately remains false: this read-only probe cannot verify tablet delivery.

The companion's status adds installed SudoVDA interface discovery, opening its interface with the current user's permissions, and Windows trust verification of the installed catalog's membership for both its INF and DLL. These checks do not send a driver IOCTL, create a monitor, or read desktop pixels. Protocol compatibility is checked only during an explicit create operation. A local trusted catalog is not a WHQL certification statement.

If interface access fails, status includes the actual Windows error, including access denied. TabletDeck does not elevate or modify device permissions. Driver installation or repair belongs to the driver's publisher/admin workflow. Microsoft's [PnPUtil documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil) describes its administrator requirement. Adding an INF package alone may not create a root-enumerated virtual device; use the publisher's supported setup rather than assuming `pnputil /add-driver` is sufficient.

## Development contract

`companion/ExtendedDisplay.cs` owns the driver handle, session identity, 400 ms watchdog, topology checks, sharing state, and bounded capture. Its constructor is inert; `Dispose()` attempts to remove the session's own monitor and closes resources. If an explicit Stop fails, sharing switches off and ownership is retained for a retry; a failed removal is not reported as stopped. No generated protocol constants or device paths are accepted from a network client.

| Route | Access | Request / result |
| --- | --- | --- |
| `GET /api/computer/display` | Paired tablet or local PC admin | Actual status; no pixels |
| `POST /api/admin/computer/display` | Local PC admin | `operation: "create"`, `width`, `height`, `refreshRate` (default 60, allowed 60–240), `acceptInstalledDriver: true`, optional `shareWithTablet` (default false) |
| Same admin route | Local PC admin | `operation: "share"`, `enabled: true/false` |
| Same admin route | Local PC admin | `operation: "remove"` |
| `POST /api/computer/display/frame` | Paired tablet or local PC admin, explicit PC sharing also required | `{ "width": 960 }`; JPEG data URI plus dimensions, sequence and timestamp |
| `POST /api/computer/display/stream/start` | Authenticated client; owned independent display and sharing required | `{ "refreshRate": 120 }`; owner-bound lease, source dimensions, requested rate, actual source rate, and `codec: "h264"` |
| `POST /api/computer/display/stream` | Same authenticated owner and unused live lease | `{ "lease": "…" }`; binary `application/vnd.deskwing.h264` response, with caching disabled |
| `POST /api/computer/display/stream/stop` | Same authenticated owner and lease | `{ "lease": "…" }`; ends only that matching stream |

Status fields include `available`, `active`, `sharedWithTablet`, `independentDesktop`, `sourceName`, `width`, `height`, actual `refreshRate`, `driver`, `detail`, and `displays`. `available` identifies an accessible, locally trusted candidate package; it does not prove a monitor was created. `active` means this process owns a driver session; `independentDesktop` requires Windows to expose that session's unique, nonoverlapping source. Frames recheck the monitor identity and bounds before and after capture. Clone, overlap, missing targets, unshared sessions, and topology changes fail closed.

`DisplayStream` runs one owner-bound native stream and one capture/encoder worker at a time, separate from control and pen requests. A start lease must attach within 5 seconds. Source identity, generation, geometry, actual refresh, sharing, pairing, and shutdown state are validated before publishing encoded data. Each H.264 access unit is bounded to 2 MiB; writes have a 2-second timeout and backpressure rather than an accumulating frame queue. A non-cooperative write retains the sole worker slot until it settles. Source refresh must meet the request, allowing 0.1 Hz tolerance for nominal rates such as 59.94 Hz.

Native apps use `/local/display/capabilities`, `/local/display/start`, and `/local/display/stop` handlers with their existing pinned PC connection. Starting requires the trusted page and foreground host; stop remains available for lifecycle cleanup. Video travels over pinned HTTPS, independently of the fast control link. Background/exit, connection failure, revoked sharing/pairing, source topology/rate changes, or decoder failure end viewing and clear the session. Restart is explicit. The app does not save streamed frames to disk.

The SudoVDA watchdog is global to that driver. TabletDeck refuses to start when that adapter already has active targets from another application. Its heartbeat runs only while its own session is alive. Do not run competing virtual-monitor managers against this same driver concurrently.

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

The preceding section is the historical 2026-11-01 baseline. New H.264 stream, encoder, native protocol, and UI checks do not retroactively establish physical display delivery. An encoder-only benchmark uses synthetic pixels and excludes monitor capture, the connection, and tablet presentation. Final integration builds and sustained 60+ fps physical playback remain separate pending checks. Record source/tablet rates, resolution, received/rendered counters, frame drops, foreground/background behavior, sharing revocation, and stop/restart with the actual build before making a performance claim.
