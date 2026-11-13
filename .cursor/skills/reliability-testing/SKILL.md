---
name: reliability-testing
description: >-
  Expert guidance for xUnit reliability tests, HttpClient fakes, SSE streaming,
  cancellation, TimeProvider, performance checks, memory diagnostics, and
  regression coverage in the Windows AI Screen Assistant. Use when editing
  ScreenAssistant.Tests, AiClient, ResponseParser, AnalysisScheduler,
  OverlayManager (Core), or when adding tests for retries, timeouts, request-id
  gating, or flaky async behavior. Do not use for WPF XAML/DPI work or for
  Windows Graphics Capture/D3D session code.
---

# Reliability Testing (Screen Assistant)

Apply this skill for Core logic and `tests/ScreenAssistant.Tests`. Test project references **Core only** (`net8.0`) — never WPF, WinRT capture, or real API keys.

## Architecture anchors

| Area | Production | Tests |
|---|---|---|
| HTTP / vision / SSE | `Core/AIClient/AiClient.cs`, `ResponseParser.cs` | `AiClientTests.cs` |
| Session loop | `Core/Scheduler/AnalysisScheduler.cs` | `AnalysisSchedulerTests.cs` |
| Overlay gating | `Core/Overlay/OverlayManager.cs` | `OverlayManagerTests.cs` |
| Packages | — | xunit 2.9.x, `Microsoft.Extensions.TimeProvider.Testing` |

Production invariants to protect:

- Shared `HttpClient` from App: `AllowAutoRedirect=false`, infinite client timeout; **per-request** cancel in `AiClient` (default 60 s).
- **No silent HTTP retries**; scheduler applies `RetryAfter` between cycles only.
- `RequiresConfigurationChange` ends the training session; transient errors continue with cooldown.
- Overlay accepts updates only for the active `requestId` while `_active`.
- Logs must not include bodies, keys, or images.

## Auto-apply when

- Adding/changing xUnit tests or test helpers (`HttpMessageHandler`, streams, fakes)
- Changing `AiClient`, SSE/`ResponseParser`, cancellation vs timeout mapping
- Changing `AnalysisScheduler` timing, overlap guard, or duration deadline
- Changing `OverlayManager` request-id / shutdown semantics
- Investigating flakes, hangs, leaks, or regressions in Core async paths

## Test style (match existing)

1. **Custom `HttpMessageHandler`** — assert method, URI, Authorization, JSON shape inside the handler; return crafted responses. No Moq required.
2. **SSE:** build `text/event-stream` bodies; use gated streams + `TaskCompletionSource` (`RunContinuationsAsynchronously`) to assert mid-stream behavior.
3. **Time:** inject `TimeProvider`; drive with `FakeTimeProvider.Advance` — never `Task.Delay` real 120 s / 50 min intervals.
4. **Async bounds:** `WaitAsync(TimeSpan.FromSeconds(5))` (or local `TestTimeout`) on TCS/tasks.
5. **Concurrency signals:** `Interlocked` / `Volatile.Read` for call counts.
6. **Distinguish** caller `CancellationToken` cancel vs client timeout (`AiException` vs `OperationCanceledException`).
7. Russian user-facing exception messages may be asserted; provider error **bodies** must stay out of exception text.

## Coverage map (keep green)

### AiClientTests

- Multimodal POST + Bearer + data-URI image + non-stream content
- Vision probe accept/reject
- Public catalog filter (image in + text out), **no** Authorization
- 429 + `Retry-After` exposed once; no repeat POST; `RequiresConfigurationChange == false`
- 401 body not leaked into exceptions
- Caller cancel vs short timeout
- HTTPS Base URL validation (reject http / userinfo / query)
- SSE partial text before completion; interrupted/`error` stream rejection
- `stream:true` with JSON body (compatible endpoint) without second request
- Cancel open stream after first chunk → OCE

### AnalysisSchedulerTests

- First analyze immediate; next after full interval
- No overlapping slow work; second `RunAsync` → `InvalidOperationException`
- Duration deadline cancels in-flight work → `SessionEnd.DurationElapsed`
- `RetryAfter` wait even when longer than interval

### OverlayManagerTests

- Show once; hide skips renders; reopen replays Ready state
- Superseded request ids ignored for append/complete/fail
- Fail/End discard partial streaming text; keep last complete answer
- `reset: true` clears previous answer; `ShutdownAsync` blocks reopen

Use sync `InlineDispatcher` + fake `IOverlayRenderer` — do not spin a real WPF `Dispatcher` in unit tests.

## When adding tests

Prefer extending these suites over new frameworks. Good next targets already visible in production:

- 429 without `Retry-After` → default ~30 s cooldown
- `RequiresConfigurationChange` matrix (4xx vs 408/429)
- SSE coalesce / max answer size / multi-line `data:` / `[DONE]`
- Huge `RetryAfter` clamped vs duration in scheduler delay
- `_running` cleared in `finally` so a later session can start

Do **not** add brittle UI automation for Graphics Capture unless the user explicitly asks; keep capture verification manual (see `windows-capture`).

## Cancellation & streaming checklist

- [ ] Cancelled analyze does not call `CompleteAsync` with partial text
- [ ] Late SSE chunks after `Complete`/`End`/`Shutdown` ignored (OverlayManager)
- [ ] Scheduler stop vs duration: correct `SessionEnd` value
- [ ] Linked tokens: deadline + caller; analyze observes session token
- [ ] `HttpClient` handler respects token (delay-until-cancel patterns in tests)

## Performance, memory, regression

- Prefer deterministic fake time over wall-clock performance asserts.
- If checking allocations/leaks in Core: short loops with handler + dispose `HttpClient`/`AiClient` inputs; avoid retaining response streams.
- Regression: any bugfix in Core should land with a failing test first when practical (id gating, retry-after, cancel-vs-timeout).
- After Core changes run the full suite, not a single test class.

## Quality gates (verify before done)

- [ ] `dotnet test .\ScreenAssistant.sln -c Release`
- [ ] New tests have no real network, disk secrets, or DPAPI dependency
- [ ] Assertions cover both success and cancel/error paths touched by the change
- [ ] No `Thread.Sleep` on production intervals; no unbounded awaits without `WaitAsync`
- [ ] Exception messages still omit provider bodies / keys
- [ ] If behavior spans MainWindow wiring, note manual check (source-lost mid-session, streaming + stop) without claiming WPF coverage

## Out of scope

- Overlay window DPI/placement/XAML → skill `wpf-desktop`
- D3D/WinRT capture session disposal → skill `windows-capture`
