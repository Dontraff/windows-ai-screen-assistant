---
name: windows-capture
description: >-
  Expert guidance for Windows Graphics Capture, WinRT, D3D11, COM interop,
  SoftwareBitmap, frame encoding, resource disposal, and capture-source loss in
  the Windows AI Screen Assistant. Use when editing GraphicsCapture, D3DDevice,
  FrameEncoding, CapturedImage, CaptureException, picker HWND binding,
  GraphicsCaptureItem.Closed, or preview/fingerprint frame pipelines. Do not use
  for overlay XAML/MVVM or for Core HTTP/scheduler unit-test patterns.
---

# Windows Capture (Screen Assistant)

Apply this skill for capture/encoding work under `src/ScreenAssistant/ScreenCapture` and the capture call sites in `MainWindow` (`Pick_Click`, `CaptureAsync`, `Source_Closed`).

## Architecture anchors

| Piece | Path | Role |
|---|---|---|
| Picker + snapshot | `ScreenCapture/GraphicsCapture.cs` | `PickAsync`, `TakeAsync`, `CopyFrameAsync` |
| D3D → WinRT device | `ScreenCapture/D3DDevice.cs` | Hardware then WARP; strict COM release |
| Encode / probe | `ScreenCapture/FrameEncoding.cs` | Scale, PNG/JPEG, SHA-256 fingerprint, vision probe |
| UI binding | `MainWindow.xaml.cs` | HWND picker, `_source`, Closed handler, preview |

**Invariant:** each snapshot is a **short-lived** D3D11 session (create → one good frame → dispose). Do not keep a long-running capture session for the 120 s analysis loop.

TFM requires `net8.0-windows10.0.19041.0` and interactive desktop. Gate with `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)` and `GraphicsCaptureSession.IsSupported()`.

## Auto-apply when

- Changing picker, HWND init, frame pool, session, cursor capture, or size recreate
- Touching D3D11CreateDevice, DXGI QI, `CreateDirect3D11DeviceFromDXGIDevice`, or `Marshal.Release`
- Handling `SoftwareBitmap`, surface copy, PNG/JPEG encode, or fingerprint skip
- Fixing timeouts, `CaptureException`, source closed/unavailable, or protected-content failures
- Moving capture work across threads (`Task.Run`, free-threaded pool, channels)

## Capture flow (preserve)

```
PickAsync(hwnd)
  GraphicsCapturePicker + InitializeWithWindow.Initialize(picker, hwnd)
  PickSingleItemAsync().AsTask(token)
  → store GraphicsCaptureItem, hook Closed

TakeAsync(item)
  Task.Run + linked 10s timeout
  CopyFrameAsync → SoftwareBitmap (using)
  FrameEncoding.EncodeAsync → CapturedImage
```

### CopyFrameAsync rules

1. Reject `item.Size` ≤ 0.
2. Format: `DirectXPixelFormat.B8G8R8A8UIntNormalized`.
3. `D3DDevice.Create()` → `Direct3D11CaptureFramePool.CreateFreeThreaded(..., bufferCount: 2, size)`.
4. `CreateCaptureSession(item)`; **`IsCursorCaptureEnabled = false`** (cursor motion must not break fingerprint skip).
5. Bounded `Channel<byte>` capacity 1, `DropWrite`, single reader — **signal only**; never take frame ownership in `FrameArrived`.
6. Loop: `ReadAsync` → `TryGetNextFrame` → `using` frame.
7. Size match → `SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Ignore)`.
8. Size change → dispose frame, `pool.Recreate(device, format, 2, size)`, wait again.
9. `finally`: unsubscribe `FrameArrived`; `using` disposes pool/session/device.

## COM / D3D interop (`D3DDevice.Create`)

Follow existing nested try/finally release order:

1. `D3D11CreateDevice` hardware (`DriverType=1`) + `D3D11_CREATE_DEVICE_BGRA_SUPPORT` (`0x20`), SDK 7.
2. On failure → WARP (`DriverType=5`).
3. `QueryInterface` DXGI device IID `54ec77fa-1377-44e6-8c32-88fd5f44c84c`.
4. `CreateDirect3D11DeviceFromDXGIDevice` → inspectable.
5. `MarshalInterface<IDirect3DDevice>.FromAbi(inspectable)`.
6. `Marshal.Release` inspectable, then dxgi, then d3d — every path.

Never leak native pointers on exception paths. Prefer extending this helper over scattering new P/Invoke.

## Encoding & fingerprint

- Long edge ≤ **2560**; Fant interpolation; keep aspect ratio.
- Fingerprint = SHA-256 of **scaled PNG bytes** (not JPEG).
- If PNG > **4 MiB** → JPEG quality 90 for the API payload; fingerprint stays PNG-based.
- Preview: `FrameEncoding.Decode` → `BitmapFrame` + **`Freeze()`** before assigning to `Image.Source`.
- Vision probe: adjacent RGB bands with distinct colors; expected string like `"RED GREEN BLUE"` for `VerifyVisionAsync`.
- Skip-identical in MainWindow uses `_lastSuccessfulFingerprint` only after **successful** analysis; failed frames stay eligible next tick.

## Cancellation, errors, source loss

| Case | Behavior |
|---|---|
| Caller cancel | `OperationCanceledException` propagates |
| 10 s timeout only | `CaptureException` («Кадр не получен за 10 секунд…») |
| Other capture failures | `CaptureException` («Выберите экран повторно») wrapping inner |
| `GraphicsCaptureItem.Closed` | UI thread: set `_sourceLost` if op uses source, cancel CTS, `DetachSource`, clear preview |
| OS / support missing | `CaptureException` with clear Russian message |

Do not swallow COM/`CaptureException` into generic UI errors without `LocalFiles.Log`.

## Resource checklist (every change)

- [ ] Every `Direct3D11CaptureFrame` in a `using` before recreate/return
- [ ] Pool, session, device, and `SoftwareBitmap` disposed on all exits
- [ ] Native COM refs released in failure paths
- [ ] No frame work on `FrameArrived` beyond `TryWrite` signal
- [ ] Capture still runs on thread-pool; UI only updates preview after await
- [ ] Cursor capture remains disabled
- [ ] Timeout vs caller cancel still distinguished

## Quality gates (verify before done)

- [ ] `dotnet build .\src\ScreenAssistant\ScreenAssistant.csproj -c Release`
- [ ] Manual: picker with MainWindow HWND; local preview; resize source if possible; stop mid-capture
- [ ] Manual: close/switch source during session → cancel, prompt re-pick, no hung «Идёт захват»
- [ ] Manual: fingerprint skip still ignores cursor-only motion
- [ ] Overlay on captured monitor may appear in frame (document, don’t “fix” by breaking capture)
- [ ] No automated WGC tests in-repo — don’t claim capture is unit-tested; prefer Core encoding helpers only if extracted without WinRT UI deps

## Out of scope

- Overlay Dispatcher/MVVM/DPI placement → skill `wpf-desktop`
- HttpClient/SSE/scheduler tests → skill `reliability-testing`
