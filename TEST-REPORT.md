# Test Coverage Handoff — Post-Demo

**Status:** 100% line+branch coverage gate is currently **not enforced**. A demo session added substantial production code without matching tests. This document is the to-do list for getting back to green.

**Scope:** Everything committed at or before `7079049` (the "v1 thesis proved" commit).

**Expected outcome when complete:** `dotnet test` and `./gradlew check` both pass with the 100% line+branch gate enforced automatically, the way they did after Round 2c merged.

## Why the gate broke

1. **CLI was multi-targeted** (`net8.0;net8.0-windows10.0.19041.0`) so test projects on plain `net8.0` can't reference it without resolver friction. Temporarily worked around by reverting `Core.Tests` to `net8.0`; that left Cli code uncovered.
2. **New production code landed with no tests:** `SessionHostLauncherAdapter`, the three adapters lifted into `Core/Session/Adapters/`, `ViewerReadyMessage` + the polymorphic-serializer registration, `RemoteIpAddress` on `IControlChannel`, `DemoActivity`, `DirectSurfaceFrameSink`.
3. **`SessionHost` gained diagnostic `Console.Error.WriteLine` lines** that aren't covered by any assertion — either remove them or route through an injectable logger and test the logger interface.
4. **CliServices.CreateDefault** was rewritten to do real wiring. Currently `[ExcludeFromCodeCoverage]` — legitimate for the platform-bound factory, but the file is bigger than just the factory method now.

## What needs doing (in priority order)

### .NET side — `WindowStream.Core.Tests` + `WindowStream.Cli.Tests`

1. **Decide the test-project TFM strategy.** Options:
    - a. Leave Core.Tests on `net8.0`, create a NEW `WindowStream.Cli.Tests` project on `net8.0-windows10.0.19041.0` that exercises Cli's production-path code. Probably cleanest.
    - b. Multi-target Core.Tests (`net8.0;net8.0-windows10.0.19041.0`) — more magic, more failure modes.
    - c. Keep Core.Tests plain `net8.0` and put CLI-specific tests in `WindowStream.Cli.Tests`. Same as (a), worded differently.
    
   Go with (a)/(c) unless there's a reason not to.

2. **Tests to write for Core.Session.Adapters/*:**
    - `TcpConnectionAcceptorAdapter`: happy-path `StartListening(0)` + `AcceptAsync` + accept a loopback connection + verify `LocalPort > 0`. Test double-dispose behavior.
    - `UdpVideoSenderAdapter`: `BindAsync` + `SendPacketAsync` to loopback + receive on a parallel `UdpClient` and verify the header + payload bytes match. Test `LocalPort`.
    - `TcpControlChannelAdapter`: round-trip a message through a TcpClient pair (server socket + client socket); verify `RemoteIpAddress` is populated after connect; verify `NotifyHeartbeatReceived` updates `LastHeartbeatReceived`; verify dispose closes the stream.

3. **Tests for `ViewerReadyMessage`:**
    - Round-trip JSON serialization (discriminator = `VIEWER_READY`, fields `streamId`/`viewerUdpPort`).
    - Include in the existing `ControlMessageSerializationTests` table.

4. **Tests for `IControlChannel.RemoteIpAddress`:**
    - The interface has a default implementation returning `null`. Default-interface-method coverage is tricky with Coverlet — if it ends up marked uncovered, exclude via attribute or a trivial fake that explicitly calls the default.

5. **Tests for `SessionHost` VIEWER_READY handling:**
    - Extend `SessionHostTests` to feed a `ViewerReadyMessage` through a fake channel that also reports a `RemoteIpAddress`. Assert the server registers the expected `IPEndPoint`.
    - Case: `ViewerReadyMessage` received but `RemoteIpAddress` is `null` → no registration, no exception.
    - Update `FakeControlChannel` in Testing/ to support setting `RemoteIpAddress`.

6. **Tests for `SessionHost` observability logs:**
    - Either: refactor `Console.Error.WriteLine` calls behind an injectable `TextWriter` (or `ILogger`) and unit-test the log interface. Then the default-production `Console.Error` writer is `[ExcludeFromCodeCoverage]`.
    - Or: simpler — remove the diagnostic logs and re-add in a later debug-infrastructure pass. They helped during the demo but aren't load-bearing.

7. **Tests for `CliServices.CreateDefault`:**
    - Currently `[ExcludeFromCodeCoverage]`. That's fine for the factory body. Verify the `WindowStream.Cli.Tests` project can still instantiate `CliServices` via the public constructor with injected fakes (the existing `FakeCliServices` in Core.Tests should move or be duplicated).

8. **Tests for `SessionHostLauncherAdapter`:**
    - The `LaunchAsync` happy path is platform-bound (WGC + NVENC) — covered by integration tests only, mark `[ExcludeFromCodeCoverage]` on the class or on `LaunchAsync`/`ProbeCaptureSizeAsync`.
    - DO unit-test the pure helpers: `AlignToEven`, the even-align-down arithmetic, the `NativeRect` struct calculations if extracted.
    - Extract `GetPhysicalWindowSize` into a class that takes an `IWin32Api` (mirror the `WindowEnumerator` pattern) so the DPI math IS testable.

### Kotlin side — `viewer/WindowStreamViewer/app/src/test/`

1. **`ControlMessage.ViewerReady` serialization** — add to the existing `ProtocolSerializationTest` round-trip suite. Mirror the C# test vectors.

2. **`ViewerPipeline` VIEWER_READY emission** — the pipeline now sends `ViewerReady` after `beginStreaming`. Unit-test that the send happens with the correct `streamId` + the receiver's `boundPort`. May require exposing a test hook on the factory.

3. **`DemoActivity` / `DirectSurfaceFrameSink`** — Android activities are typically excluded from Kover; do the same here with documented rationale (matches the `MainActivity` exclusion). `DirectSurfaceFrameSink` is a thin wrapper with one method worth testing — `onFrameRendered` increments the counter, `acquireSurface` returns the injected surface. Trivially testable with JUnit + a mock Surface.

### Gate re-enforcement

Once the above lands, re-enable the Coverlet gate on Core.Tests (the thresholds in the csproj are already there) and verify `dotnet test` in the solution root reports 100% / 100% / 100%. Same for `./gradlew check` on the viewer side.

## Files that changed in the demo commit, by coverage status

```
FILE                                                          | COVERAGE STATUS
--------------------------------------------------------------+----------------
src/WindowStream.Cli/CliServices.cs                           | partial (factory excluded)
src/WindowStream.Cli/WindowStream.Cli.csproj                  | n/a
src/WindowStream.Cli/Hosting/SessionHostLauncherAdapter.cs    | uncovered — needs platform-bound exclusion + extracted-helper tests
src/WindowStream.Core/Protocol/ControlMessage.cs              | polymorphic attr only — coverage via derived types
src/WindowStream.Core/Protocol/ViewerReadyMessage.cs          | uncovered — needs serialization round-trip test
src/WindowStream.Core/Session/IControlChannel.cs              | default-interface-method, needs care
src/WindowStream.Core/Session/SessionHost.cs                  | VIEWER_READY branch uncovered, diag-log lines uncovered
src/WindowStream.Core/Session/Adapters/*.cs                   | uncovered — need loopback tests
viewer/.../viewer/control/ProtocolMessages.kt                 | ViewerReady uncovered
viewer/.../viewer/app/ViewerPipeline.kt                       | VIEWER_READY emission uncovered
viewer/.../viewer/demo/*.kt                                   | exclude via Kover filter + rationale
viewer/.../AndroidManifest.xml                                | n/a
```

## Estimated effort

- C# side: 4–6 hours for someone fresh. Adapter loopback tests are the bulk.
- Kotlin side: 1–2 hours. Mostly adding one message type to an existing round-trip suite.
- Restructuring decisions (new Cli.Tests project, injectable logger): 30 min.

Good candidate for a single sonnet agent working from this report.
