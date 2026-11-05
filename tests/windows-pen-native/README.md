# Native Windows pen diagnostic

Run explicitly on an unlocked Windows desktop:

```powershell
dotnet run --project tests/windows-pen-native/WindowsPenNativeTests.csproj -c Release -- --run-native
```

The diagnostic opens one disposable receiver window and sends pen events only to that window. It aborts if another window owns the foreground or covers a sample position. It creates the native device on one worker thread and injects on other workers to exercise the companion's thread-pool dispatch model. It verifies the actual `GetPointerPenInfo` values for screen coordinates, pressure, tilt, down, move and up, then removes its synthetic pointer and closes. It does not inspect or draw in a user document and does not change the companion's pen settings.

For a tablet-driven drawing test, use the separate manual mode:

```powershell
dotnet run --project tests/windows-pen-native/WindowsPenNativeTests.csproj -c Release -- --manual
```

Manual mode never generates synthetic input. It remains open as **DeskWing pen test canvas**, draws incoming Windows pen strokes with pressure-sensitive width, displays pressure and tilt, and prints completed-stroke measurements to the console. Select this application as the companion's pen target, focus the test canvas, then start the session on the tablet. **Clear canvas** clears only this temporary drawing; closing the window discards it. Mouse and touch do not draw.

This interactive hardware/API diagnostic is intentionally separate from unattended regression. Passing proves the native Windows API path on the tested PC; it does not certify compatibility with Photoshop, Microsoft Whiteboard, Wintab configurations or a particular tablet.

API reference: [InjectSyntheticPointerInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injectsyntheticpointerinput) and [WM_POINTERDOWN](https://learn.microsoft.com/en-us/windows/win32/inputmsg/wm-pointerdown).
