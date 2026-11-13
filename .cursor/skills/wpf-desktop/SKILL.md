---
name: wpf-desktop
description: >-
  Expert guidance for WPF, MVVM, Dispatcher, XAML, Per-Monitor V2 DPI,
  multi-monitor window placement, UI lifecycle, and accessibility in the
  Windows AI Screen Assistant. Use when editing App, MainWindow, OverlayWindow,
  OverlayViewModel, WpfOverlayRenderer, OverlayPreferences, XAML, app.manifest,
  AutomationProperties, or any UI-thread / floating-window behavior. Do not use
  for Graphics Capture/D3D capture pipelines or for Core-only xUnit tests.
---

# WPF Desktop (Screen Assistant)

Apply this skill for UI/shell changes in `src/ScreenAssistant`. Keep Core UI-free; bridge only through `IOverlayService` / `IOverlayRenderer` / `IOverlayDispatcher`.

## Architecture anchors

| Piece | Path | Role |
|---|---|---|
| Composition root | `App.xaml.cs` | Shared `HttpClient`, wire overlay + `MainWindow` |
| Main shell | `MainWindow.xaml(.cs)` | Operations, settings, session orchestration (code-behind) |
| Overlay MVVM | `Overlay/OverlayViewModel.cs`, `OverlayWindow.xaml(.cs)` | Bound floating chrome |
| WPF adapters | `Overlay/WpfOverlayRenderer.cs` | `IOverlayRenderer` + `WpfOverlayDispatcher` |
| State machine | `ScreenAssistant.Core/Overlay/OverlayManager.cs` | Request-id gating; call only via dispatcher |
| Prefs | `Overlay/OverlayPreferences.cs` | `%LOCALAPPDATA%\ScreenAssistant\overlay.json` |
| DPI | `app.manifest` | Per-Monitor V2 |

Composition (preserve unless redesign is explicit):

1. `OverlayPreferences.Load()`
2. `WpfOverlayRenderer` + debounced save
3. `OverlayManager(WpfOverlayDispatcher(Dispatcher), renderer)`
4. `MainWindow(AiClient, AnalysisScheduler, overlay, enabled, SetEnabledPreference)`
5. `renderer.Attach(main)`; `CloseRequested` → `main.DisableOverlay()`

## Auto-apply when

- Changing XAML, window chrome, topmost/transparent overlay, or main tabs
- Touching `Dispatcher`, `InvokeAsync`, `CheckAccess`, or UI updates from capture/AI callbacks
- Placement, drag, restore, monitor change, or DIP↔pixel conversion
- Accessibility labels, live regions, focus, or keyboard paths
- Closing/shutdown while an operation runs (`OnClosing`, CTS, `ShutdownAsync`)

## Hard rules

1. **UI mutations only on the UI thread.** Overlay updates go through `IOverlayDispatcher` (`Background`). If already on UI thread, run inline (see `WpfOverlayDispatcher`).
2. **Do not put WPF types in Core.** New UI behavior: extend contracts in Core, implement in `Overlay/` or `MainWindow`.
3. **One operation CTS** (`MainWindow._operation`). No concurrent picker/preview/catalog/session.
4. **Overlay close = disable**, not silent hide: chrome close → `CloseRequested` → uncheck `OverlayEnabled`.
5. **Closing mid-op:** cancel CTS, defer close (`_closing`), finish op, then `Close()`; idle path calls `_overlay.ShutdownAsync()`.
6. **Source-lost and Win32/display messages** marshal with `Dispatcher.InvokeAsync` before touching controls.
7. **AI work off UI:** keep `Task.Run` around `AiClient` analyze paths; streaming deltas may call `AppendAsync` (dispatcher) and must no-op when cancelled.
8. Prefer incremental fixes over MVVM rewrites of `MainWindow` unless the user asks for restructuring.

## MVVM & XAML conventions

- **Overlay:** real MVVM — `OverlayViewModel` + `INotifyPropertyChanged`; MainWindow «Оверлей» tab binds the same VM via `BindOverlaySettings`.
- **MainWindow:** intentional code-behind + named controls; settings via `UserSettings` / `KeyStore`.
- Preferences are immutable records (`with`); appearance changes raise `PreferencesChanged` → 400 ms `DispatcherTimer` debounce in `WpfOverlayRenderer`.
- Compact overlay shows first line only (`CompactText`); full answer stays in MainWindow «Последний ответ».
- Russian UI strings; status brushes: idle slate / active sea-green / error firebrick.

## DPI & multi-monitor

- Manifest: Per-Monitor V2 — keep it.
- Win32 work-area rects are **physical pixels**; WPF `Width`/`Height` are **DIP**. Convert with `VisualTreeHelper.GetDpi(this)` (see `OverlayWindow.Place`).
- Placement: `MonitorFromPoint` / `MonitorFromWindow` / `GetMonitorInfo` / `SetWindowPos` with `SWP_NOSIZE|NOZORDER|NOACTIVATE`.
- Default: top-right of **main window’s** monitor (`maxX - 20`, `area.Top + 20`); restore clamps saved X/Y into work area.
- Hook `WM_DISPLAYCHANGE` (0x007E) and `WM_SETTINGCHANGE` (0x001A) → `KeepOnScreen`.
- After `DragMove`, re-`Place` on current monitor and persist via `SetPosition`.
- Re-place after Loaded and when Width/DisplayHeight change.

## Accessibility checklist

- Keep `AutomationProperties.LiveSetting="Polite"` on capture status, main status, and overlay answer text.
- Named interactive controls; `Label.Target` for form fields.
- Overlay: `ShowActivated=False`; don’t steal focus from the user’s work.
- Opaque/transparent modes: when panel opacity is low, keep drag targets usable (existing header opacity / `TextInset` logic).
- Errors that open/fail overlay window: catch `InvalidOperationException`/`Win32Exception`, uncheck overlay, log `overlay-window`.

## OverlayManager contracts (UI side)

Call order from session code:

- Probe/session start: `BeginAsync(..., reset: true)`
- Each frame: `BeginAsync` → optional `AppendAsync` → `CompleteAsync` / `FailAsync`
- Stop/end: `EndAsync`; app close: `ShutdownAsync`

Never render superseded `requestId` chunks after stop. Manager already gates; UI must still skip append when `token` is cancelled.

## Quality gates (verify before done)

- [ ] Build: `dotnet build .\ScreenAssistant.sln -c Release`
- [ ] No Core project references to WPF/WinRT UI assemblies
- [ ] All overlay/control updates path through dispatcher or UI event handlers
- [ ] Overlay toggle, user-close, and `DisableOverlay` stay consistent with `overlay.json` `Enabled`
- [ ] Position survives restart; reset returns to main-monitor top-right
- [ ] Drag/resize stays inside work area after DPI and monitor change
- [ ] Closing main window during session cancels work and does not deadlock
- [ ] Live regions / names still present after XAML edits
- [ ] Manual: floating answer during stream; reopen mid-response; second monitor; stop then ignore late chunks

## Out of scope

- `ScreenCapture/*` device/session details → skill `windows-capture`
- xUnit, `HttpClient` fakes, `FakeTimeProvider` → skill `reliability-testing`
