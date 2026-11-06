# DeskWing application icon

The source is `app-icon.svg`: four rounded pads on the existing Studio charcoal background, three cyan and one ivory. It uses the application's existing colors, without lettering or gradients.

Run `scripts/Build-Icons.ps1` on Windows to render the PNG preview, seven-size ICO (16–256px), and desktop favicon from that SVG. These are deterministic vector renders, not generated photographs or AI bitmap assets. The Android adaptive foreground uses the same rounded pads with safe-zone inset; Android 13+ also has a monochrome layer for themed icons.

Windows executable, installer, window, tray and shortcuts share the ICO. Android uses adaptive icon resources. All icon artwork is original project artwork under the repository license.
