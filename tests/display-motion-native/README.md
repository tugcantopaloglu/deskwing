# Owned-display motion diagnostic

This optional local helper paints a high-contrast moving pattern and frame counter across an explicitly selected nonprimary display. It creates no display, captures no screen, injects no input, and does not move other applications. Run it only against the source and physical-pixel bounds returned for DeskWing's owned virtual display.

Build and run noninteractive checks:

```powershell
dotnet build tests/display-motion-native/DisplayMotionTests.csproj -c Release
dotnet run --project tests/display-motion-native/DisplayMotionTests.csproj -c Release --no-build -- --self-test
```

After the companion has created its virtual monitor, supply its exact source and bounds:

```powershell
# Replace both placeholders with the companion's actual owned-display values.
dotnet run --project tests/display-motion-native/DisplayMotionTests.csproj -c Release --no-build -- --source '<owned source>' --bounds '<x,y,width,height>' --hz 120 --seconds 60
```

Supported pacing targets are 60, 120 and 165 Hz. The helper uses a high-resolution waitable timer and permits only one queued repaint; late frames are dropped rather than replayed. Console output and the window report actual paint calls per second and skipped UI frames. These are **not** encoded, decoded, displayed, or panel refresh rates; use the tablet's physical motion and companion transport metrics together to assess the end-to-end result.

The window fills only the supplied monitor, does not request foreground activation, and closes automatically after 60 seconds by default. The optional duration is bounded to 5–600 seconds. Focus its own window and press Escape to close earlier, or use its taskbar close action. A source/bounds change or removal stops the helper instead of moving the pattern to a different display. No user content is saved.
