# Windows H.264 encoder checks

`dotnet run --project tests/display-encoder/DisplayEncoderTests.csproj -c Release` runs pixel-conversion, bounds and native layout checks without capture, input injection or encoder activation.

Add `-- --native` to activate Windows' built-in H.264 encoder on synthetic moving pixels. Options include `--width 1920 --height 1080 --rate 144 --frames 240`. `--output <path>` optionally writes the synthetic Annex B bitstream for independent decoder inspection. The probe never reads the desktop or any window. It checks one output access unit per input, unchanged capture timestamps, bounded frames and SPS/PPS on each IDR.

The production encoder uses constrained Baseline, no B frames, a one-second GOP and low-complexity slice encoding. It is currently the Windows software encoder, not an advertised GPU encoder. It needs no bundled FFmpeg or third-party codec. Each instance belongs to one stream worker thread and accepts bounded positive-stride BGRA frames; capture authorization belongs to the separate display service.

Local synthetic measurements on 2026-11-05 (Ryzen 5 7500F): 1280×800 at a requested 60 Hz encoded 600 frames with 2.82 ms mean and 5.32 ms p95 time for color conversion plus encoding. A 1920×1080/144 Hz probe encoded 240 frames with 5.45 ms mean and 10.60 ms p95. These measurements exclude capture, transport, decoding and screen presentation, so they do not establish end-to-end refresh rate. The separate stream and tablet counters must report actual performance.

The 1080p synthetic stream decoded as 240 constrained-Baseline frames using an independent local FFmpeg installation. Raw Annex B has no transport timestamps; supply the requested input rate when inspecting it (`ffmpeg -r 144 -i <path> -f null NUL`). The live protocol carries capture timestamps explicitly.

Interface declarations are derived from the installed Windows SDK headers. Encoding follows Microsoft's [H.264 encoder documentation](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder), [sample processing contract](https://learn.microsoft.com/en-us/windows/win32/medfound/processing-data-in-the-encoder) and [Annex B sequence header format](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mt-mpeg-sequence-header-attribute).
