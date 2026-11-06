# Low-latency control in 1.4

Version 1.4 removes repeated connection setup and polling delays from the normal control path. It cannot make network transmission, touch sampling, display refresh, app startup, audio buffers, or capture instantaneous.

Version 1.4.1 corrects APK asset paths for Android. A physical Android 16 tablet subsequently connected over both Wi-Fi and USB; sparse heartbeat observations were 3.9–10.5 ms over USB and 6.5 ms over Wi-Fi. These are individual connection samples, not action-latency benchmarks. See the [physical test report and limits](LIVE-TEST.md).

## Measured software overhead

These command-line measurements use one Windows PC and a synthetic OBS server. No tablet, Wi-Fi, USB cable, actual OBS, or audio output is involved.

| Measurement | 1.3 baseline | 1.4 optimized |
| --- | ---: | ---: |
| Median command round-trip | 7.468 ms | 0.582 ms |
| 95th percentile command round-trip | 10.606 ms | 1.646 ms |
| Median state fetch with a nominal 12 ms delay per OBS response | 100.851 ms | 14.969 ms |
| 95th percentile state fetch in that fixture | 105.498 ms | 21.072 ms |

Commands use 100 measured samples after warm-up; state fetches use 25. The baseline creates a fresh HTTPS/TLS connection for every action to model the old Android transport. The optimized path uses one authenticated persistent TLS connection. Both execute a fixture scene command and wait for acknowledgement. The new release's cold-HTTPS path still measures about 7.7 ms here; connection reuse supplies most of the command improvement.

These are process-to-process measurements, not touch-to-scene or touch-to-sound latency. They do not establish a sub-millisecond Wi-Fi, USB, or Android guarantee. Machine load, hardware, and broadcast conditions were not controlled as a lab experiment. Qualified benchmark summaries are included in `Latency-Results.json` in the release package.

## Enable the fast path

Update **both the APK and Windows companion**, including USB helpers. Existing pairing and the PC certificate remain valid.

| TCP port | Purpose |
| --- | --- |
| 8740 | Local PC editor only |
| 8743 | Pinned HTTPS pairing, board/library/settings, and fallback control |
| 8744 | Pinned persistent TLS commands and pushed state |

On Wi-Fi, allow the companion on the private network. If a firewall rule previously allowed only port 8743, also allow TCP 8744 for this app on the private network. Do not forward these ports through a router.

Open the tablet's Connection settings to see whether the persistent encrypted link is connected and, after a heartbeat, its last network round-trip time. That figure does not include touch/display/audio delay. A blocked fast port leaves authenticated HTTPS fallback usable.

Use a stable local link with good signal. A wired PC connection to the same router reduces one wireless hop. Congested channels, mesh/repeater hops, VPN routing, and a busy access point can add delay or jitter. The app does not change router, firewall, channel, or Windows power settings automatically.

While paired and in the foreground on Wi-Fi, the app requests Android's low-latency Wi-Fi mode on Android 10+ and high-performance mode on Android 8–9. It releases the request when backgrounded, switched to USB, or unpaired. The normal `WAKE_LOCK` permission supports that radio lock; no microphone or location permission is added. Hardware/driver support determines its effect, and power use may increase while active. See Android's [low-latency documentation](https://source.android.com/docs/core/connect/wifi-low-latency) and [Wi-Fi lock reference](https://developer.android.com/reference/android/net/wifi/WifiManager.WifiLock).

For USB, run the updated **USB-Connect.cmd**, or enable automatic USB reconnection. Both now create two reverse tunnels:

```powershell
.\platform-tools\adb.exe -s YOUR_DEVICE_SERIAL reverse tcp:8743 tcp:8743
.\platform-tools\adb.exe -s YOUR_DEVICE_SERIAL reverse tcp:8744 tcp:8744
```

Select USB in the tablet's settings. A data cable, debugging authorization, and any required driver remain necessary. The same PC certificate is pinned through the tunnel. USB avoids radio congestion but still has device/ADB scheduling overhead.

## What changed

- **Persistent control:** buttons, holds, OBS faders/mute, and app-volume/mute reuse an authenticated TLS connection with TCP_NODELAY. Frames are bounded, length-prefixed JSON. No extra Android libraries were added.
- **Isolated work:** slow board/library requests use a separate Android worker pool. HTTPS fallback reuses its pinned TLS factory and connection pool.
- **Pushed state:** OBS scene/input/output events invalidate state immediately. Native tablets receive updates on the control link; PC panels use an authenticated event stream. Five-second snapshots keep the link fresh. Polling remains a fallback.
- **Parallel OBS reads:** independent scene, stream, recording, volume, and mute reads run concurrently. Failed connections briefly back off rather than forcing every simultaneous read through another slow connect.
- **Live sliders:** OBS and app faders send while dragging, with one request per slider in flight and only the newest unsent value retained. The final value waits for acknowledgement.
- **Less UI work:** state patches update labels and active markers without rebuilding the whole board. Completing an action does not wait for a full board refresh. Older state revisions cannot replace newer snapshots.
- **Lean Soundpad lookup:** linked sounds still resolve a fresh sound list before playing, preserving safe remapping, but skip the unrelated category query. Soundpad and audio routing still contribute their own latency.

## Disconnect and safety behavior

Only paired tablet credentials authenticate on the fast port. Admin routes and arbitrary PC targets are unavailable. Stream/record confirmation, volume limits, hold leases, and revocation checks still apply. Revoking a tablet closes its live connection.

After a connection fails, a sent command's result can be unknown. Pending requests fail and are **not** automatically replayed, because repeating a toggle or app launch could perform it twice. New user presses are new requests. Recent duplicate request IDs are refused on the same connection.

Held keys release when their owning connection ends or their lease expires. Closing an older connection cannot release a newer lease. Reconnection uses backoff; disconnected commands are not queued for later execution.

## Reproduce without opening apps

```powershell
$env:TABLETDECK_COMPANION = (Resolve-Path .\dist\Windows\DeskWing-Companion.exe).Path
npm run test:fast --prefix .\tests
$env:TABLETDECK_BENCH_LABEL = 'local-optimized'
$env:TABLETDECK_BENCH_FAST = '1'
npm run benchmark:latency --prefix .\tests
```

The fast suite starts a hidden command-line companion and fixture OBS server. It compiles/runs the same platform-only Java transport on the desktop JDK, including pin rejection, framing, pause/resume, and no-replay checks. Set `JAVA_HOME` or retain the documented repository toolchain. It does not start an Android app or browser.

For a baseline against a retained 1.3 executable, point `TABLETDECK_COMPANION` there, choose another label, and unset `TABLETDECK_BENCH_FAST`. Results go to ignored `.test-output/latency`. Benchmark ports are 18840/18843/18844 and 19555; the fast suite uses 18940/18943/18944 and 19565.

Real Wi-Fi/USB jitter, Android Wi-Fi-lock behavior, touch-to-output latency, audible playback, and broadcasting still need a rehearsal on your devices. GUI apps and computer-use tools were not used for these measurements.
