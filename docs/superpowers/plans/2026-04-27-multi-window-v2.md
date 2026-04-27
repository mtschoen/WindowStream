# Multi-Window v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement WindowStream v2: one coordinator process per host advertises all windows; viewer picks; coordinator spawns one isolated worker process per active stream so a native crash takes down only that stream. Single TCP + single UDP per server, multiplexed by `streamId`. Pause + focus relay + load shedding. Hard `version=2` break.

**Architecture:** Coordinator (existing `windowstream serve`, rewritten) owns mDNS, TCP control, UDP send, window enumeration, worker supervision, focus relay, load shedding. Workers (new `windowstream worker` subcommand) do only WGC capture + NVENC encode and emit chunks over a per-stream named pipe. v1's `SessionHost` retires.

**Tech Stack:** .NET 8, FFmpeg.AutoGen (NVENC), CsWinRT (WGC), `NamedPipeServerStream`/`NamedPipeClientStream` for IPC, `System.Text.Json` polymorphic serialization, xUnit + Coverlet (100% line+branch gate), Kotlin + Jetpack Compose for the viewer.

**Spec:** `docs/superpowers/specs/2026-04-27-multi-window-design.md`

**Phasing:** Six phases. Each phase commits a coherent unit; the build stays green between phases. Server side (Phases 1–4) completes before viewer side (Phase 5). Phase 6 is manual acceptance.

---

## File Structure

### Server-side (.NET) — files created

- `src/WindowStream.Core/Protocol/WindowDescriptor.cs` — descriptive record for a window in the coordinator's enumeration.
- `src/WindowStream.Core/Protocol/WindowAddedMessage.cs`
- `src/WindowStream.Core/Protocol/WindowRemovedMessage.cs`
- `src/WindowStream.Core/Protocol/WindowUpdatedMessage.cs`
- `src/WindowStream.Core/Protocol/WindowSnapshotMessage.cs`
- `src/WindowStream.Core/Protocol/ListWindowsMessage.cs`
- `src/WindowStream.Core/Protocol/OpenStreamMessage.cs`
- `src/WindowStream.Core/Protocol/CloseStreamMessage.cs`
- `src/WindowStream.Core/Protocol/PauseStreamMessage.cs`
- `src/WindowStream.Core/Protocol/ResumeStreamMessage.cs`
- `src/WindowStream.Core/Protocol/FocusWindowMessage.cs`
- `src/WindowStream.Core/Protocol/StreamStoppedReason.cs` (enum + name converter)
- `src/WindowStream.Core/Hosting/WorkerChunkPipe.cs` — bidirectional named-pipe wrapper.
- `src/WindowStream.Core/Hosting/WorkerChunkFrame.cs` — typed worker→coordinator frame record.
- `src/WindowStream.Core/Hosting/WorkerCommandFrame.cs` — typed coordinator→worker frame record.
- `src/WindowStream.Core/Hosting/IWorkerProcessLauncher.cs`
- `src/WindowStream.Core/Hosting/WorkerProcessLauncher.cs` — production launcher that does `Process.Start`.
- `src/WindowStream.Core/Hosting/WorkerSupervisor.cs`
- `src/WindowStream.Core/Hosting/StreamRouter.cs`
- `src/WindowStream.Core/Hosting/LoadShedder.cs`
- `src/WindowStream.Core/Hosting/CoordinatorOptions.cs`
- `src/WindowStream.Core/Session/CoordinatorControlServer.cs` — replacement for the accept-loop logic in `SessionHost`.
- `src/WindowStream.Core/Session/Input/FocusRelay.cs`
- `src/WindowStream.Core/Session/Input/IForegroundWindowApi.cs` — Win32 surface mockable for unit tests.
- `src/WindowStream.Core/Session/Input/ForegroundWindowApi.cs` — production P/Invoke implementation.
- `src/WindowStream.Cli/Commands/WorkerCommandHandler.cs` — `windowstream worker` subcommand entry point.
- `src/WindowStream.Cli/Commands/WorkerArguments.cs`
- `src/WindowStream.Cli/Hosting/CoordinatorLauncher.cs` — replaces `SessionHostLauncherAdapter`.
- `tests/WindowStream.Core.Tests/Protocol/...` — round-trip tests for every new message + reason.
- `tests/WindowStream.Core.Tests/Capture/WindowEnumeratorDiffTests.cs`
- `tests/WindowStream.Core.Tests/Hosting/...` — `WorkerChunkPipeTests`, `WorkerSupervisorTests`, `StreamRouterTests`, `LoadShedderTests`.
- `tests/WindowStream.Core.Tests/Session/CoordinatorControlServerTests.cs`
- `tests/WindowStream.Core.Tests/Session/Input/FocusRelayTests.cs`
- `tests/WindowStream.Integration.Tests/Loopback/CoordinatorLoopbackHarness.cs` — multi-stream replacement for `SessionHostLoopbackHarness`.
- `tests/WindowStream.Integration.Tests/Loopback/CoordinatorMultiStreamTests.cs`

### Server-side — files modified

- `src/WindowStream.Core/Protocol/ControlMessage.cs` — register all new derived types.
- `src/WindowStream.Core/Protocol/ServerHelloMessage.cs` — `ActiveStream` field replaced by `Windows: WindowDescriptor[]`.
- `src/WindowStream.Core/Protocol/StreamStartedMessage.cs` — add `WindowId`.
- `src/WindowStream.Core/Protocol/StreamStoppedMessage.cs` — add `Reason`.
- `src/WindowStream.Core/Protocol/ViewerReadyMessage.cs` — drop `StreamId`.
- `src/WindowStream.Core/Protocol/KeyEventMessage.cs` — add `StreamId`.
- `src/WindowStream.Core/Protocol/ProtocolErrorCode.cs` + `ProtocolErrorCodeNames.cs` — add `EncoderCapacity`, `WindowNotFound`, `StreamNotFound`.
- `src/WindowStream.Core/Capture/Windows/WindowEnumerator.cs` — extend with diff/event-emission method.
- `src/WindowStream.Core/Discovery/AdvertisementOptions.cs` (and friends) — bump `version=2` default; drop `-<port>` from instance name composition (callers control that).
- `src/WindowStream.Cli/Program.cs` + `RootCommandBuilder.cs` — register the new `worker` subcommand; rewire `serve` to launch the coordinator.
- `src/WindowStream.Cli/Commands/ServeCommandHandler.cs` — drop `--hwnd` and `--title-matches`; new behavior is "launch coordinator and wait."
- `src/WindowStream.Cli/Hosting/SessionHostLauncherAdapter.cs` — DELETE (replaced by `CoordinatorLauncher`).

### Server-side — files deleted

- `src/WindowStream.Core/Session/SessionHost.cs` — replaced by Worker (capture+encode pump) + CoordinatorControlServer (TCP/UDP/heartbeat).
- `src/WindowStream.Core/Session/SessionHostOptions.cs` — split into `CoordinatorOptions` and worker argv.

### Viewer-side (Kotlin) — files created

- `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/control/WindowDescriptor.kt`
- `viewer/.../control/WindowEvent.kt` — sealed class for Added/Removed/Updated.
- `viewer/.../app/ui/WindowPickerScreen.kt` — replaces `ServerPickerScreen` and `ServerSelectionActivity`.
- `viewer/.../app/ui/PanelSwitcherActivity.kt` — replaces `DemoActivity`.
- `viewer/.../transport/StreamMultiplexer.kt` — fans `EncodedFrame` flow by `streamId`.
- `viewer/.../control/MultiStreamControlClient.kt`
- `viewer/.../app/MultiStreamViewerPipeline.kt`

### Viewer-side — files modified

- `viewer/.../control/ProtocolMessages.kt` — add new message types, drop `streamId` from `ViewerReady`, add `streamId` to `KeyEvent`, add `reason` to `StreamStopped`, restructure `ServerHello`.
- `viewer/.../control/ProtocolSerialization.kt` — register new discriminators.
- `viewer/.../transport/UdpTransportReceiver.kt` — emit `EncodedFrame` with `streamId` (already in packet header; just propagate).
- `viewer/.../transport/EncodedFrame.kt` — add `streamId` field.
- `viewer/.../app/MainActivity.kt` — route to new picker.

### Viewer-side — files deleted

- `viewer/.../app/ui/ServerPickerScreen.kt`
- `viewer/.../app/ServerSelectionActivity.kt` (if present)
- `viewer/.../demo/DemoActivity.kt` — replaced by PanelSwitcherActivity.

---

## Phase 1 — Protocol foundation

Goal: introduce all new message types, refactor existing ones, bump version. After this phase, the .NET tree compiles green with all-new tests; nothing executes the new shapes yet (coordinator still runs the old SessionHost). End-of-phase commit leaves SessionHost partially broken because some message shapes change incompatibly — that's expected; Phase 2 starts replacing it.

### Task 1.1: WindowDescriptor type + tests

**Files:**
- Create: `src/WindowStream.Core/Protocol/WindowDescriptor.cs`
- Create: `tests/WindowStream.Core.Tests/Protocol/WindowDescriptorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/WindowStream.Core.Tests/Protocol/WindowDescriptorTests.cs
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public class WindowDescriptorTests
{
    [Fact]
    public void WindowDescriptor_HoldsAllFields()
    {
        WindowDescriptor descriptor = new WindowDescriptor(
            windowId: 42,
            hwnd: 1574208,
            processId: 9876,
            processName: "devenv",
            title: "Solution1 - Microsoft Visual Studio",
            physicalWidth: 1920,
            physicalHeight: 1080);

        Assert.Equal(42UL, descriptor.WindowId);
        Assert.Equal(1574208L, descriptor.Hwnd);
        Assert.Equal(9876, descriptor.ProcessId);
        Assert.Equal("devenv", descriptor.ProcessName);
        Assert.Equal("Solution1 - Microsoft Visual Studio", descriptor.Title);
        Assert.Equal(1920, descriptor.PhysicalWidth);
        Assert.Equal(1080, descriptor.PhysicalHeight);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WindowDescriptorTests
```
Expected: FAIL — "type or namespace name 'WindowDescriptor' could not be found."

- [ ] **Step 3: Create the type**

```csharp
// src/WindowStream.Core/Protocol/WindowDescriptor.cs
namespace WindowStream.Core.Protocol;

public sealed record WindowDescriptor(
    ulong WindowId,
    long Hwnd,
    int ProcessId,
    string ProcessName,
    string Title,
    int PhysicalWidth,
    int PhysicalHeight);
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WindowDescriptorTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Protocol/WindowDescriptor.cs tests/WindowStream.Core.Tests/Protocol/WindowDescriptorTests.cs
git commit -m "feat(protocol): add WindowDescriptor type for v2 enumeration"
```

### Task 1.2: New error codes (EncoderCapacity, WindowNotFound, StreamNotFound)

**Files:**
- Modify: `src/WindowStream.Core/Protocol/ProtocolErrorCode.cs`
- Modify: `src/WindowStream.Core/Protocol/ProtocolErrorCodeNames.cs`
- Modify: `tests/WindowStream.Core.Tests/Protocol/ProtocolErrorCodeTests.cs`

- [ ] **Step 1: Add failing tests**

In `ProtocolErrorCodeTests.cs`, add:

```csharp
[Theory]
[InlineData(ProtocolErrorCode.EncoderCapacity, "ENCODER_CAPACITY")]
[InlineData(ProtocolErrorCode.WindowNotFound, "WINDOW_NOT_FOUND")]
[InlineData(ProtocolErrorCode.StreamNotFound, "STREAM_NOT_FOUND")]
public void NewV2Codes_RoundTrip(ProtocolErrorCode code, string wireName)
{
    Assert.Equal(wireName, ProtocolErrorCodeNames.ToWireName(code));
    Assert.Equal(code, ProtocolErrorCodeNames.Parse(wireName));
}
```

- [ ] **Step 2: Run tests, verify failure**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~ProtocolErrorCodeTests
```
Expected: FAIL — `EncoderCapacity` does not exist on the enum.

- [ ] **Step 3: Add enum members**

In `ProtocolErrorCode.cs`:

```csharp
public enum ProtocolErrorCode
{
    VersionMismatch,
    ViewerBusy,
    WindowGone,
    CaptureFailed,
    EncodeFailed,
    MalformedMessage,
    EncoderCapacity,
    WindowNotFound,
    StreamNotFound
}
```

In `ProtocolErrorCodeNames.cs` add the three new arms in both `ToWireName` and `Parse` switches.

- [ ] **Step 4: Run tests, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~ProtocolErrorCodeTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Protocol/ProtocolErrorCode.cs src/WindowStream.Core/Protocol/ProtocolErrorCodeNames.cs tests/WindowStream.Core.Tests/Protocol/ProtocolErrorCodeTests.cs
git commit -m "feat(protocol): add EncoderCapacity / WindowNotFound / StreamNotFound error codes"
```

### Task 1.3: StreamStoppedReason enum + name converter

**Files:**
- Create: `src/WindowStream.Core/Protocol/StreamStoppedReason.cs`
- Create: `src/WindowStream.Core/Protocol/StreamStoppedReasonNames.cs`
- Create: `tests/WindowStream.Core.Tests/Protocol/StreamStoppedReasonTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/WindowStream.Core.Tests/Protocol/StreamStoppedReasonTests.cs
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public class StreamStoppedReasonTests
{
    [Theory]
    [InlineData(StreamStoppedReason.ClosedByViewer, "CLOSED_BY_VIEWER")]
    [InlineData(StreamStoppedReason.WindowGone, "WINDOW_GONE")]
    [InlineData(StreamStoppedReason.EncoderFailed, "ENCODER_FAILED")]
    [InlineData(StreamStoppedReason.CaptureFailed, "CAPTURE_FAILED")]
    [InlineData(StreamStoppedReason.StreamHung, "STREAM_HUNG")]
    [InlineData(StreamStoppedReason.ServerShutdown, "SERVER_SHUTDOWN")]
    public void StreamStoppedReason_RoundTrips(StreamStoppedReason reason, string wireName)
    {
        Assert.Equal(wireName, StreamStoppedReasonNames.ToWireName(reason));
        Assert.Equal(reason, StreamStoppedReasonNames.Parse(wireName));
    }

    [Fact]
    public void Parse_UnknownReason_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => StreamStoppedReasonNames.Parse("bogus"));
    }
}
```

- [ ] **Step 2: Run test, verify failure**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~StreamStoppedReasonTests
```
Expected: FAIL.

- [ ] **Step 3: Create the enum + name converter**

```csharp
// src/WindowStream.Core/Protocol/StreamStoppedReason.cs
namespace WindowStream.Core.Protocol;

public enum StreamStoppedReason
{
    ClosedByViewer,
    WindowGone,
    EncoderFailed,
    CaptureFailed,
    StreamHung,
    ServerShutdown
}
```

```csharp
// src/WindowStream.Core/Protocol/StreamStoppedReasonNames.cs
using System;

namespace WindowStream.Core.Protocol;

public static class StreamStoppedReasonNames
{
    public static string ToWireName(StreamStoppedReason reason) => reason switch
    {
        StreamStoppedReason.ClosedByViewer => "CLOSED_BY_VIEWER",
        StreamStoppedReason.WindowGone => "WINDOW_GONE",
        StreamStoppedReason.EncoderFailed => "ENCODER_FAILED",
        StreamStoppedReason.CaptureFailed => "CAPTURE_FAILED",
        StreamStoppedReason.StreamHung => "STREAM_HUNG",
        StreamStoppedReason.ServerShutdown => "SERVER_SHUTDOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown reason")
    };

    public static StreamStoppedReason Parse(string wireName) => wireName switch
    {
        "CLOSED_BY_VIEWER" => StreamStoppedReason.ClosedByViewer,
        "WINDOW_GONE" => StreamStoppedReason.WindowGone,
        "ENCODER_FAILED" => StreamStoppedReason.EncoderFailed,
        "CAPTURE_FAILED" => StreamStoppedReason.CaptureFailed,
        "STREAM_HUNG" => StreamStoppedReason.StreamHung,
        "SERVER_SHUTDOWN" => StreamStoppedReason.ServerShutdown,
        _ => throw new ArgumentException($"unknown stream-stopped reason: {wireName}", nameof(wireName))
    };
}
```

- [ ] **Step 4: Run test, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~StreamStoppedReasonTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Protocol/StreamStoppedReason.cs src/WindowStream.Core/Protocol/StreamStoppedReasonNames.cs tests/WindowStream.Core.Tests/Protocol/StreamStoppedReasonTests.cs
git commit -m "feat(protocol): add StreamStoppedReason enum + name converter"
```

### Task 1.4: Refactor existing messages

This task changes shapes of `ServerHelloMessage`, `StreamStartedMessage`, `StreamStoppedMessage`, `ViewerReadyMessage`, `KeyEventMessage`. The serialization roundtrip tests in `ControlMessageSerializationTests.cs` will need updates. Existing integration tests using `SessionHost` will break — that's expected; they get repaired in later phases.

**Files:**
- Modify: `src/WindowStream.Core/Protocol/ServerHelloMessage.cs`
- Modify: `src/WindowStream.Core/Protocol/StreamStartedMessage.cs`
- Modify: `src/WindowStream.Core/Protocol/StreamStoppedMessage.cs`
- Modify: `src/WindowStream.Core/Protocol/ViewerReadyMessage.cs`
- Modify: `src/WindowStream.Core/Protocol/KeyEventMessage.cs`
- Modify: `tests/WindowStream.Core.Tests/Protocol/ControlMessageSerializationTests.cs`

- [ ] **Step 1: Add failing roundtrip tests**

In `ControlMessageSerializationTests.cs` add (or replace existing roundtrip tests for these types):

```csharp
[Fact]
public void ServerHello_RoundTripsWithWindowsList()
{
    WindowDescriptor[] windows = new[]
    {
        new WindowDescriptor(1UL, 0x100, 99, "notepad", "Untitled - Notepad", 800, 600),
        new WindowDescriptor(2UL, 0x200, 100, "devenv", "WindowStream.sln", 1920, 1080)
    };
    ServerHelloMessage original = new ServerHelloMessage(serverVersion: 2, windows: windows);

    string serialized = ControlMessageSerialization.Serialize(original);
    ControlMessage deserialized = ControlMessageSerialization.Deserialize(serialized);

    ServerHelloMessage typed = Assert.IsType<ServerHelloMessage>(deserialized);
    Assert.Equal(2, typed.ServerVersion);
    Assert.Equal(2, typed.Windows.Length);
    Assert.Equal(1UL, typed.Windows[0].WindowId);
    Assert.Equal("Untitled - Notepad", typed.Windows[0].Title);
}

[Fact]
public void StreamStarted_RoundTripsWithWindowId()
{
    StreamStartedMessage original = new StreamStartedMessage(
        streamId: 7,
        windowId: 42UL,
        codec: "h264",
        width: 1920,
        height: 1080,
        framesPerSecond: 60);

    string serialized = ControlMessageSerialization.Serialize(original);
    StreamStartedMessage typed = Assert.IsType<StreamStartedMessage>(ControlMessageSerialization.Deserialize(serialized));

    Assert.Equal(7, typed.StreamId);
    Assert.Equal(42UL, typed.WindowId);
}

[Fact]
public void StreamStopped_RoundTripsWithReason()
{
    StreamStoppedMessage original = new StreamStoppedMessage(streamId: 3, reason: StreamStoppedReason.EncoderFailed);
    StreamStoppedMessage typed = Assert.IsType<StreamStoppedMessage>(
        ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
    Assert.Equal(3, typed.StreamId);
    Assert.Equal(StreamStoppedReason.EncoderFailed, typed.Reason);
}

[Fact]
public void ViewerReady_RoundTripsWithoutStreamId()
{
    ViewerReadyMessage original = new ViewerReadyMessage(viewerUdpPort: 12345);
    ViewerReadyMessage typed = Assert.IsType<ViewerReadyMessage>(
        ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
    Assert.Equal(12345, typed.ViewerUdpPort);
}

[Fact]
public void KeyEvent_RoundTripsWithStreamId()
{
    KeyEventMessage original = new KeyEventMessage(streamId: 5, keyCode: 0x41, isUnicode: true, isDown: true);
    KeyEventMessage typed = Assert.IsType<KeyEventMessage>(
        ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
    Assert.Equal(5, typed.StreamId);
    Assert.Equal(0x41, typed.KeyCode);
    Assert.True(typed.IsUnicode);
    Assert.True(typed.IsDown);
}
```

Also: REMOVE any pre-existing tests that assert the old shape (e.g., a test that builds `ServerHelloMessage` with `ActiveStream: ActiveStreamInformation`; or a `ViewerReadyMessage` test with a `streamId`).

- [ ] **Step 2: Add a `JsonNeedsConverter` for `StreamStoppedReason`**

This requires a converter registered on `ControlMessageSerialization.Options`. Either:
- (a) Use `[JsonConverter(typeof(StreamStoppedReasonConverter))]` on the property, OR
- (b) Register the converter globally in `Options.Converters`.

Option (b) matches the existing pattern used for `ProtocolErrorCode`.

In `ControlMessageSerialization.cs`, in the `Options` initializer, add to `Converters`:

```csharp
new StreamStoppedReasonConverter()
```

Add the converter as a private nested class:

```csharp
private sealed class StreamStoppedReasonConverter : JsonConverter<StreamStoppedReason>
{
    public override StreamStoppedReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? wireName = reader.GetString();
        if (wireName is null) throw new JsonException("null is not a valid stream-stopped reason");
        return StreamStoppedReasonNames.Parse(wireName);
    }

    public override void Write(Utf8JsonWriter writer, StreamStoppedReason value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(StreamStoppedReasonNames.ToWireName(value));
    }
}
```

- [ ] **Step 3: Refactor the messages**

```csharp
// src/WindowStream.Core/Protocol/ServerHelloMessage.cs
namespace WindowStream.Core.Protocol;

public sealed record ServerHelloMessage(
    int ServerVersion,
    WindowDescriptor[] Windows) : ControlMessage;
```

```csharp
// src/WindowStream.Core/Protocol/StreamStartedMessage.cs
namespace WindowStream.Core.Protocol;

public sealed record StreamStartedMessage(
    int StreamId,
    ulong WindowId,
    string Codec,
    int Width,
    int Height,
    int FramesPerSecond) : ControlMessage;
```

(Note: `UdpPort` field retires — UDP port lives in mDNS / `SERVER_HELLO` for the connection, not per stream.)

Wait — re-check: the spec says coordinator owns one UDP socket. The viewer learns the server's UDP port how? In v1 it came in `STREAM_STARTED.UdpPort`. In v2 we should put it in `SERVER_HELLO` because it's connection-level, not stream-level.

Update plan: add `udpPort` to `ServerHelloMessage`. Adjust the test in Step 1 to assert it.

Replace `ServerHelloMessage` with:

```csharp
// src/WindowStream.Core/Protocol/ServerHelloMessage.cs
namespace WindowStream.Core.Protocol;

public sealed record ServerHelloMessage(
    int ServerVersion,
    int UdpPort,
    WindowDescriptor[] Windows) : ControlMessage;
```

And replace the `ServerHello_RoundTripsWithWindowsList` test in Step 1 to construct with `udpPort: 64000` and assert `typed.UdpPort == 64000`.

```csharp
// src/WindowStream.Core/Protocol/StreamStoppedMessage.cs
namespace WindowStream.Core.Protocol;

public sealed record StreamStoppedMessage(
    int StreamId,
    StreamStoppedReason Reason) : ControlMessage;
```

```csharp
// src/WindowStream.Core/Protocol/ViewerReadyMessage.cs
namespace WindowStream.Core.Protocol;

public sealed record ViewerReadyMessage(int ViewerUdpPort) : ControlMessage;
```

```csharp
// src/WindowStream.Core/Protocol/KeyEventMessage.cs
namespace WindowStream.Core.Protocol;

public sealed record KeyEventMessage(int StreamId, int KeyCode, bool IsUnicode, bool IsDown) : ControlMessage;
```

- [ ] **Step 4: Build and run tests**

```bash
dotnet build src/WindowStream.Core/WindowStream.Core.csproj
```
Expected: build succeeds for `WindowStream.Core` (existing `SessionHost` consumers in `WindowStream.Cli` and integration tests will break — that's fine; we'll fix in later phases by removing `SessionHost`. For now, we want `WindowStream.Core` itself to compile + tests in `WindowStream.Core.Tests` to pass).

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
```
Expected: PASS for all protocol tests. Coverage gate may already trip — that's OK; we're partway through the phase.

If `SessionHost.cs` causes `WindowStream.Core` itself not to build (it consumes `ActiveStreamInformation` and `ViewerReadyMessage.StreamId`), we have a choice: either fix the SessionHost consumers in this task to use new shapes, or delete SessionHost now. Choose the simpler path: in `SessionHost.cs`, replace `ActiveStream`-typed fields with the new shape and adjust `BuildActiveStreamDescriptor` to be unused / commented out (or delete the method); replace `viewerReady.ViewerUdpPort` reference (drop the `streamId` reference); leave the remaining logic. **Mark this as a temporary patch** — Phase 2 deletes `SessionHost` entirely.

- [ ] **Step 5: Commit**

```bash
git add -A src/WindowStream.Core/Protocol src/WindowStream.Core/Session/SessionHost.cs tests/WindowStream.Core.Tests/Protocol
git commit -m "refactor(protocol): v2 shapes — windows list in SERVER_HELLO, streamId on KEY_EVENT, reason on STREAM_STOPPED"
```

### Task 1.5: New v2 message types — register all eleven

These are pure-data records + their `ControlMessage.cs` registration. Group them into one task — they're all the same shape work.

**Files:**
- Create: `src/WindowStream.Core/Protocol/WindowAddedMessage.cs`
- Create: `src/WindowStream.Core/Protocol/WindowRemovedMessage.cs`
- Create: `src/WindowStream.Core/Protocol/WindowUpdatedMessage.cs`
- Create: `src/WindowStream.Core/Protocol/WindowSnapshotMessage.cs`
- Create: `src/WindowStream.Core/Protocol/ListWindowsMessage.cs`
- Create: `src/WindowStream.Core/Protocol/OpenStreamMessage.cs`
- Create: `src/WindowStream.Core/Protocol/CloseStreamMessage.cs`
- Create: `src/WindowStream.Core/Protocol/PauseStreamMessage.cs`
- Create: `src/WindowStream.Core/Protocol/ResumeStreamMessage.cs`
- Create: `src/WindowStream.Core/Protocol/FocusWindowMessage.cs`
- Modify: `src/WindowStream.Core/Protocol/ControlMessage.cs`
- Create: `tests/WindowStream.Core.Tests/Protocol/V2MessageRoundTripTests.cs`

- [ ] **Step 1: Add the failing roundtrip test for all eleven messages**

```csharp
// tests/WindowStream.Core.Tests/Protocol/V2MessageRoundTripTests.cs
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public class V2MessageRoundTripTests
{
    private static T RoundTrip<T>(T message) where T : ControlMessage
    {
        string serialized = ControlMessageSerialization.Serialize(message);
        ControlMessage deserialized = ControlMessageSerialization.Deserialize(serialized);
        return Assert.IsType<T>(deserialized);
    }

    [Fact]
    public void WindowAdded_RoundTrips()
    {
        WindowDescriptor descriptor = new WindowDescriptor(1UL, 0x100, 99, "notepad", "test", 800, 600);
        WindowAddedMessage typed = RoundTrip(new WindowAddedMessage(descriptor));
        Assert.Equal(1UL, typed.Window.WindowId);
        Assert.Equal("test", typed.Window.Title);
    }

    [Fact]
    public void WindowRemoved_RoundTrips()
    {
        WindowRemovedMessage typed = RoundTrip(new WindowRemovedMessage(windowId: 7UL));
        Assert.Equal(7UL, typed.WindowId);
    }

    [Fact]
    public void WindowUpdated_RoundTrips()
    {
        WindowUpdatedMessage typed = RoundTrip(new WindowUpdatedMessage(
            windowId: 7UL, title: "new title", physicalWidth: 1280, physicalHeight: 720));
        Assert.Equal(7UL, typed.WindowId);
        Assert.Equal("new title", typed.Title);
        Assert.Equal(1280, typed.PhysicalWidth);
        Assert.Equal(720, typed.PhysicalHeight);
    }

    [Fact]
    public void WindowUpdated_OptionalFieldsAcceptNull()
    {
        WindowUpdatedMessage typed = RoundTrip(new WindowUpdatedMessage(
            windowId: 7UL, title: null, physicalWidth: null, physicalHeight: null));
        Assert.Null(typed.Title);
        Assert.Null(typed.PhysicalWidth);
        Assert.Null(typed.PhysicalHeight);
    }

    [Fact]
    public void WindowSnapshot_RoundTrips()
    {
        WindowDescriptor[] windows = new[] { new WindowDescriptor(1UL, 0x100, 99, "n", "t", 800, 600) };
        WindowSnapshotMessage typed = RoundTrip(new WindowSnapshotMessage(windows));
        Assert.Single(typed.Windows);
    }

    [Fact]
    public void ListWindows_RoundTrips()
    {
        ListWindowsMessage typed = RoundTrip(new ListWindowsMessage());
        Assert.NotNull(typed);
    }

    [Fact]
    public void OpenStream_RoundTrips()
    {
        OpenStreamMessage typed = RoundTrip(new OpenStreamMessage(windowId: 42UL));
        Assert.Equal(42UL, typed.WindowId);
    }

    [Fact]
    public void CloseStream_RoundTrips()
    {
        CloseStreamMessage typed = RoundTrip(new CloseStreamMessage(streamId: 3));
        Assert.Equal(3, typed.StreamId);
    }

    [Fact]
    public void PauseStream_RoundTrips()
    {
        PauseStreamMessage typed = RoundTrip(new PauseStreamMessage(streamId: 3));
        Assert.Equal(3, typed.StreamId);
    }

    [Fact]
    public void ResumeStream_RoundTrips()
    {
        ResumeStreamMessage typed = RoundTrip(new ResumeStreamMessage(streamId: 3));
        Assert.Equal(3, typed.StreamId);
    }

    [Fact]
    public void FocusWindow_RoundTrips()
    {
        FocusWindowMessage typed = RoundTrip(new FocusWindowMessage(streamId: 3));
        Assert.Equal(3, typed.StreamId);
    }
}
```

- [ ] **Step 2: Run, verify failure**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~V2MessageRoundTripTests
```
Expected: FAIL — type-not-found errors for all eleven types.

- [ ] **Step 3: Create the eleven message records**

Each in its own file (following the project's "one type per file" rule):

```csharp
// WindowAddedMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record WindowAddedMessage(WindowDescriptor Window) : ControlMessage;
```

```csharp
// WindowRemovedMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record WindowRemovedMessage(ulong WindowId) : ControlMessage;
```

```csharp
// WindowUpdatedMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record WindowUpdatedMessage(
    ulong WindowId,
    string? Title,
    int? PhysicalWidth,
    int? PhysicalHeight) : ControlMessage;
```

```csharp
// WindowSnapshotMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record WindowSnapshotMessage(WindowDescriptor[] Windows) : ControlMessage;
```

```csharp
// ListWindowsMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record ListWindowsMessage() : ControlMessage;
```

```csharp
// OpenStreamMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record OpenStreamMessage(ulong WindowId) : ControlMessage;
```

```csharp
// CloseStreamMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record CloseStreamMessage(int StreamId) : ControlMessage;
```

```csharp
// PauseStreamMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record PauseStreamMessage(int StreamId) : ControlMessage;
```

```csharp
// ResumeStreamMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record ResumeStreamMessage(int StreamId) : ControlMessage;
```

```csharp
// FocusWindowMessage.cs
namespace WindowStream.Core.Protocol;
public sealed record FocusWindowMessage(int StreamId) : ControlMessage;
```

- [ ] **Step 4: Register all derived types in `ControlMessage.cs`**

Replace the `[JsonDerivedType]` attribute block in `ControlMessage.cs` with the v2 set:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), typeDiscriminator: "HELLO")]
[JsonDerivedType(typeof(ServerHelloMessage), typeDiscriminator: "SERVER_HELLO")]
[JsonDerivedType(typeof(StreamStartedMessage), typeDiscriminator: "STREAM_STARTED")]
[JsonDerivedType(typeof(StreamStoppedMessage), typeDiscriminator: "STREAM_STOPPED")]
[JsonDerivedType(typeof(RequestKeyframeMessage), typeDiscriminator: "REQUEST_KEYFRAME")]
[JsonDerivedType(typeof(HeartbeatMessage), typeDiscriminator: "HEARTBEAT")]
[JsonDerivedType(typeof(ErrorMessage), typeDiscriminator: "ERROR")]
[JsonDerivedType(typeof(ViewerReadyMessage), typeDiscriminator: "VIEWER_READY")]
[JsonDerivedType(typeof(KeyEventMessage), typeDiscriminator: "KEY_EVENT")]
[JsonDerivedType(typeof(WindowAddedMessage), typeDiscriminator: "WINDOW_ADDED")]
[JsonDerivedType(typeof(WindowRemovedMessage), typeDiscriminator: "WINDOW_REMOVED")]
[JsonDerivedType(typeof(WindowUpdatedMessage), typeDiscriminator: "WINDOW_UPDATED")]
[JsonDerivedType(typeof(WindowSnapshotMessage), typeDiscriminator: "WINDOW_SNAPSHOT")]
[JsonDerivedType(typeof(ListWindowsMessage), typeDiscriminator: "LIST_WINDOWS")]
[JsonDerivedType(typeof(OpenStreamMessage), typeDiscriminator: "OPEN_STREAM")]
[JsonDerivedType(typeof(CloseStreamMessage), typeDiscriminator: "CLOSE_STREAM")]
[JsonDerivedType(typeof(PauseStreamMessage), typeDiscriminator: "PAUSE_STREAM")]
[JsonDerivedType(typeof(ResumeStreamMessage), typeDiscriminator: "RESUME_STREAM")]
[JsonDerivedType(typeof(FocusWindowMessage), typeDiscriminator: "FOCUS_WINDOW")]
public abstract record ControlMessage;
```

- [ ] **Step 5: Run, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~V2MessageRoundTripTests
```
Expected: PASS for all eleven roundtrip tests.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Protocol tests/WindowStream.Core.Tests/Protocol/V2MessageRoundTripTests.cs
git commit -m "feat(protocol): add v2 control messages (WINDOW_*, STREAM_*, FOCUS_WINDOW)"
```

### Task 1.6: Bump protocol version + retire ActiveStreamInformation

**Files:**
- Delete: `src/WindowStream.Core/Protocol/ActiveStreamInformation.cs`
- Modify: `src/WindowStream.Core/Discovery/AdvertisementOptions.cs` (or wherever the default `protocolMajorVersion` lives — search for `ProtocolMajorVersion` if the field is on a different record).

- [ ] **Step 1: Run `grep` to find every ActiveStreamInformation reference**

```bash
grep -rn "ActiveStreamInformation" src tests
```
Note all hits — they all need to be removed or rewritten.

- [ ] **Step 2: Delete the file and rewrite consumers**

```bash
git rm src/WindowStream.Core/Protocol/ActiveStreamInformation.cs
```

Wherever `ActiveStreamInformation` was constructed, replace with the new `ServerHelloMessage(serverVersion, udpPort, windows)` shape (most consumers will be deleted in Phase 2 anyway). Aim for: `WindowStream.Core` builds clean; consumers in `WindowStream.Cli` and integration tests may produce build errors — those get fixed in Phase 2 when we delete `SessionHost`.

- [ ] **Step 3: Bump `protocolMajorVersion` defaults**

Search the codebase for hardcoded `protocolMajorVersion: 1`:

```bash
grep -rn "protocolMajorVersion: 1\|protocolRevision: 0" src
```

Replace `protocolMajorVersion: 1` with `protocolMajorVersion: 2` in `SessionHostLauncherAdapter.cs` (the now-deprecated production wiring). Don't worry about consumers that get deleted in Phase 2.

- [ ] **Step 4: Build core; expected: green**

```bash
dotnet build src/WindowStream.Core/WindowStream.Core.csproj
```
Expected: PASS.

- [ ] **Step 5: Build integration tests; expected: red**

```bash
dotnet build tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj
```
Expected: build errors related to `ActiveStreamInformation`, `SessionHost`, etc. **This is OK.** Comment out / `[Skip]` the broken tests with a `// TODO(v2): re-enable in Phase 4 with CoordinatorLoopbackHarness` marker so they don't fail the build. Concrete approach: at the top of `SessionHostLoopbackEndToEndTests.cs`, add `[CollectionDefinition("v1-disabled")]` and a `Skip` argument on the affected `[Fact]`s, OR rename the file to `SessionHostLoopbackEndToEndTests.cs.bak` and `git rm` it. Pick the latter (cleaner; we're deleting `SessionHost` in Phase 2 anyway).

```bash
git rm tests/WindowStream.Integration.Tests/Loopback/SessionHostLoopbackEndToEndTests.cs
git rm tests/WindowStream.Integration.Tests/Loopback/SessionHostLoopbackHarness.cs
```

(The `DecodedVideoFrame.cs` and `SoftwareDecoder.cs` files in the same directory are still useful — keep them.)

```bash
dotnet build tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj
```
Expected: PASS now that the v1 loopback tests are gone.

- [ ] **Step 6: Verify Core test coverage gate still green**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
```
Expected: PASS (including coverage gate).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(protocol): retire ActiveStreamInformation; bump protocolMajorVersion to 2; remove v1 loopback tests pending v2 harness"
```

---

## Phase 2 — Worker process + IPC

Goal: extract today's capture+encode pump into a standalone `windowstream worker` subcommand that talks to a parent over a named pipe. After this phase, you can spawn a worker by hand and verify it streams encoded NAL units over a pipe.

### Task 2.1: WorkerChunkFrame + WorkerCommandFrame types + tests

**Files:**
- Create: `src/WindowStream.Core/Hosting/WorkerChunkFrame.cs`
- Create: `src/WindowStream.Core/Hosting/WorkerCommandFrame.cs`
- Create: `src/WindowStream.Core/Hosting/WorkerCommandTag.cs`
- Create: `tests/WindowStream.Core.Tests/Hosting/WorkerFrameTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/WindowStream.Core.Tests/Hosting/WorkerFrameTests.cs
using System;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerFrameTests
{
    [Fact]
    public void WorkerChunkFrame_HoldsPayloadAndMetadata()
    {
        byte[] payload = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1F };
        WorkerChunkFrame frame = new WorkerChunkFrame(
            presentationTimestampMicroseconds: 16_667UL,
            isKeyframe: true,
            payload: payload);

        Assert.Equal(16_667UL, frame.PresentationTimestampMicroseconds);
        Assert.True(frame.IsKeyframe);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public void WorkerCommandFrame_DefaultsAreEmpty()
    {
        WorkerCommandFrame frame = new WorkerCommandFrame(WorkerCommandTag.Pause);
        Assert.Equal(WorkerCommandTag.Pause, frame.Tag);
    }

    [Theory]
    [InlineData(WorkerCommandTag.Pause, (byte)0x01)]
    [InlineData(WorkerCommandTag.Resume, (byte)0x02)]
    [InlineData(WorkerCommandTag.RequestKeyframe, (byte)0x03)]
    [InlineData(WorkerCommandTag.Shutdown, (byte)0xFF)]
    public void WorkerCommandTag_HasStableWireValue(WorkerCommandTag tag, byte expected)
    {
        Assert.Equal(expected, (byte)tag);
    }
}
```

- [ ] **Step 2: Run, verify failure**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WorkerFrameTests
```
Expected: FAIL.

- [ ] **Step 3: Create the types**

```csharp
// src/WindowStream.Core/Hosting/WorkerCommandTag.cs
namespace WindowStream.Core.Hosting;

public enum WorkerCommandTag : byte
{
    Pause = 0x01,
    Resume = 0x02,
    RequestKeyframe = 0x03,
    Shutdown = 0xFF
}
```

```csharp
// src/WindowStream.Core/Hosting/WorkerChunkFrame.cs
namespace WindowStream.Core.Hosting;

public sealed record WorkerChunkFrame(
    ulong PresentationTimestampMicroseconds,
    bool IsKeyframe,
    byte[] Payload);
```

```csharp
// src/WindowStream.Core/Hosting/WorkerCommandFrame.cs
namespace WindowStream.Core.Hosting;

public sealed record WorkerCommandFrame(WorkerCommandTag Tag);
```

- [ ] **Step 4: Run, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WorkerFrameTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Hosting tests/WindowStream.Core.Tests/Hosting/WorkerFrameTests.cs
git commit -m "feat(hosting): worker chunk + command frame records"
```

### Task 2.2: WorkerChunkPipe — wire format

**Files:**
- Create: `src/WindowStream.Core/Hosting/WorkerChunkPipe.cs`
- Create: `tests/WindowStream.Core.Tests/Hosting/WorkerChunkPipeTests.cs`

The wire format (from spec):
- Worker → coordinator: `uint32 length BE | uint64 ptsUs BE | uint8 flags | byte[length] payload`
- Coordinator → worker: `uint8 commandTag | <no payload in v2.0>`

We expose two methods that take a `Stream`:
- `Task WriteChunkAsync(Stream, WorkerChunkFrame, CancellationToken)`
- `Task<WorkerChunkFrame> ReadChunkAsync(Stream, CancellationToken)`
- `Task WriteCommandAsync(Stream, WorkerCommandFrame, CancellationToken)`
- `Task<WorkerCommandFrame> ReadCommandAsync(Stream, CancellationToken)`

The methods are static; no per-pipe state. Pipe management itself lives outside this type.

- [ ] **Step 1: Write failing tests using `MemoryStream` round-trip**

```csharp
// tests/WindowStream.Core.Tests/Hosting/WorkerChunkPipeTests.cs
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerChunkPipeTests
{
    [Fact]
    public async Task ChunkRoundTripsThroughMemoryStream()
    {
        WorkerChunkFrame original = new WorkerChunkFrame(
            presentationTimestampMicroseconds: 0xDEADBEEFCAFEUL,
            isKeyframe: true,
            payload: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerChunkFrame read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);

        Assert.Equal(original.PresentationTimestampMicroseconds, read.PresentationTimestampMicroseconds);
        Assert.Equal(original.IsKeyframe, read.IsKeyframe);
        Assert.Equal(original.Payload, read.Payload);
    }

    [Fact]
    public async Task NonKeyframeChunkRoundTrips()
    {
        WorkerChunkFrame original = new WorkerChunkFrame(
            presentationTimestampMicroseconds: 100UL,
            isKeyframe: false,
            payload: new byte[] { 0xFF });

        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerChunkFrame read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);
        Assert.False(read.IsKeyframe);
    }

    [Fact]
    public async Task EmptyPayloadRoundTrips()
    {
        WorkerChunkFrame original = new WorkerChunkFrame(0UL, false, System.Array.Empty<byte>());
        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerChunkFrame read = await WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None);
        Assert.Empty(read.Payload);
    }

    [Theory]
    [InlineData(WorkerCommandTag.Pause)]
    [InlineData(WorkerCommandTag.Resume)]
    [InlineData(WorkerCommandTag.RequestKeyframe)]
    [InlineData(WorkerCommandTag.Shutdown)]
    public async Task CommandRoundTrips(WorkerCommandTag tag)
    {
        WorkerCommandFrame original = new WorkerCommandFrame(tag);
        using MemoryStream stream = new MemoryStream();
        await WorkerChunkPipe.WriteCommandAsync(stream, original, CancellationToken.None);
        stream.Position = 0;
        WorkerCommandFrame read = await WorkerChunkPipe.ReadCommandAsync(stream, CancellationToken.None);
        Assert.Equal(tag, read.Tag);
    }

    [Fact]
    public async Task ReadChunk_OnTruncatedHeader_Throws()
    {
        using MemoryStream stream = new MemoryStream(new byte[] { 0x00, 0x01 }); // partial length
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => WorkerChunkPipe.ReadChunkAsync(stream, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run, verify failure**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WorkerChunkPipeTests
```
Expected: FAIL.

- [ ] **Step 3: Implement `WorkerChunkPipe`**

```csharp
// src/WindowStream.Core/Hosting/WorkerChunkPipe.cs
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public static class WorkerChunkPipe
{
    public static async Task WriteChunkAsync(Stream stream, WorkerChunkFrame frame, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4 + 8 + 1];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)frame.Payload.Length));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4, 8), frame.PresentationTimestampMicroseconds);
        header[12] = (byte)(frame.IsKeyframe ? 0x01 : 0x00);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (frame.Payload.Length > 0)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<WorkerChunkFrame> ReadChunkAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4 + 8 + 1];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        ulong pts = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4, 8));
        bool isKeyframe = (header[12] & 0x01) != 0;
        byte[] payload = new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
        return new WorkerChunkFrame(pts, isKeyframe, payload);
    }

    public static async Task WriteCommandAsync(Stream stream, WorkerCommandFrame command, CancellationToken cancellationToken)
    {
        byte[] tag = new byte[] { (byte)command.Tag };
        await stream.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WorkerCommandFrame> ReadCommandAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return new WorkerCommandFrame((WorkerCommandTag)buffer[0]);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"pipe closed after {total} of {buffer.Length} bytes");
            }
            total += read;
        }
    }
}
```

- [ ] **Step 4: Run, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WorkerChunkPipeTests
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Hosting/WorkerChunkPipe.cs tests/WindowStream.Core.Tests/Hosting/WorkerChunkPipeTests.cs
git commit -m "feat(hosting): WorkerChunkPipe wire-format read/write"
```

### Task 2.3: Worker entry point — `windowstream worker` subcommand

The worker process: parses argv, opens the named pipe (client), configures NVENC encoder + WGC capture against the given HWND, drains capture frames into the encoder, writes encoded chunks to the pipe. Reads coordinator commands (Pause/Resume/RequestKeyframe/Shutdown) on the same pipe (it's a `NamedPipeClientStream` opened bidirectionally).

**Files:**
- Create: `src/WindowStream.Cli/Commands/WorkerArguments.cs`
- Create: `src/WindowStream.Cli/Commands/WorkerCommandHandler.cs`
- Modify: `src/WindowStream.Cli/RootCommandBuilder.cs` (register the `worker` subcommand)
- Modify: `src/WindowStream.Cli/Program.cs` (route)
- Modify: `src/WindowStream.Cli/CliServices.cs` / `ICliServices.cs` (if the existing DI surface needs a hook for the worker handler)

- [ ] **Step 1: Define WorkerArguments record**

```csharp
// src/WindowStream.Cli/Commands/WorkerArguments.cs
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;

namespace WindowStream.Cli.Commands;

public sealed record WorkerArguments(
    WindowHandle Hwnd,
    int StreamId,
    string PipeName,
    EncoderOptions EncoderOptions);
```

- [ ] **Step 2: Implement WorkerCommandHandler**

```csharp
// src/WindowStream.Cli/Commands/WorkerCommandHandler.cs
#if WINDOWS
using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;

namespace WindowStream.Cli.Commands;

public sealed class WorkerCommandHandler
{
    public async Task<int> ExecuteAsync(WorkerArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            using NamedPipeClientStream pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: arguments.PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource lifecycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            object pauseLock = new object();
            bool paused = false;

            await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
            encoder.Configure(arguments.EncoderOptions);

            WgcCaptureSource captureSource = new WgcCaptureSource();

            Task commandReaderTask = Task.Run(async () =>
            {
                try
                {
                    while (!lifecycle.Token.IsCancellationRequested)
                    {
                        WorkerCommandFrame command = await WorkerChunkPipe.ReadCommandAsync(pipe, lifecycle.Token);
                        switch (command.Tag)
                        {
                            case WorkerCommandTag.Pause:
                                lock (pauseLock) paused = true;
                                break;
                            case WorkerCommandTag.Resume:
                                lock (pauseLock) paused = false;
                                encoder.RequestKeyframe();
                                break;
                            case WorkerCommandTag.RequestKeyframe:
                                encoder.RequestKeyframe();
                                break;
                            case WorkerCommandTag.Shutdown:
                                lifecycle.Cancel();
                                return;
                        }
                    }
                }
                catch (OperationCanceledException) { /* normal */ }
                catch (System.IO.EndOfStreamException) { lifecycle.Cancel(); }
            }, lifecycle.Token);

            Task encodeOutputTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (EncodedChunk chunk in encoder.EncodedChunks.WithCancellation(lifecycle.Token))
                    {
                        WorkerChunkFrame frame = new WorkerChunkFrame(
                            presentationTimestampMicroseconds: (ulong)chunk.presentationTimestampMicroseconds,
                            isKeyframe: chunk.isKeyframe,
                            payload: chunk.payload.ToArray());
                        await WorkerChunkPipe.WriteChunkAsync(pipe, frame, lifecycle.Token);
                    }
                }
                catch (OperationCanceledException) { /* normal */ }
            }, lifecycle.Token);

            await using IWindowCapture capture = captureSource.Start(
                arguments.Hwnd,
                new CaptureOptions(targetFramesPerSecond: arguments.EncoderOptions.framesPerSecond, includeCursor: false),
                lifecycle.Token);

            try
            {
                await foreach (CapturedFrame captured in capture.Frames.WithCancellation(lifecycle.Token))
                {
                    bool currentlyPaused;
                    lock (pauseLock) currentlyPaused = paused;
                    if (currentlyPaused) continue;
                    await encoder.EncodeAsync(captured, lifecycle.Token);
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception captureException)
            {
                Console.Error.WriteLine($"[worker] capture failed: {captureException}");
                lifecycle.Cancel();
                try { await Task.WhenAll(commandReaderTask, encodeOutputTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                return 2; // CaptureFailed
            }

            lifecycle.Cancel();
            try { await Task.WhenAll(commandReaderTask, encodeOutputTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            return 0;
        }
        catch (Exception unexpected)
        {
            Console.Error.WriteLine($"[worker] unexpected: {unexpected}");
            return 3;
        }
    }
}
#endif
```

(EncodeFailed exit code = 1 will be plumbed in if `EncoderAsync` ever throws — left at 3 for unexpected errors. Refine in Task 2.4 if needed.)

- [ ] **Step 3: Wire up in `RootCommandBuilder.cs`**

Read `src/WindowStream.Cli/RootCommandBuilder.cs` to find how `serve` and `list` are registered. Mirror that pattern for `worker`. The worker subcommand options:
- `--hwnd <long>` (required)
- `--stream-id <int>` (required)
- `--pipe-name <string>` (required)
- `--encoder-options <json>` (required) — opaque JSON blob deserialized to `EncoderOptions`

Use `System.CommandLine`'s `Option<T>`. The handler:

```csharp
Command workerCommand = new Command("worker", "Internal: capture + encode pump for a single stream. Spawned by coordinator; not for direct invocation.");
Option<long> hwndOption = new Option<long>("--hwnd") { IsRequired = true };
Option<int> streamIdOption = new Option<int>("--stream-id") { IsRequired = true };
Option<string> pipeNameOption = new Option<string>("--pipe-name") { IsRequired = true };
Option<string> encoderOptionsJsonOption = new Option<string>("--encoder-options") { IsRequired = true };
workerCommand.AddOption(hwndOption);
workerCommand.AddOption(streamIdOption);
workerCommand.AddOption(pipeNameOption);
workerCommand.AddOption(encoderOptionsJsonOption);
workerCommand.SetHandler(async (long hwnd, int streamId, string pipeName, string encoderOptionsJson, CancellationToken ct) =>
{
    EncoderOptions encoderOptions = System.Text.Json.JsonSerializer.Deserialize<EncoderOptions>(encoderOptionsJson)
        ?? throw new InvalidOperationException("could not parse encoder options");
    WorkerArguments arguments = new WorkerArguments(
        new WindowHandle(hwnd), streamId, pipeName, encoderOptions);
#if WINDOWS
    return await new WorkerCommandHandler().ExecuteAsync(arguments, ct);
#else
    Console.Error.WriteLine("worker subcommand requires Windows");
    return 4;
#endif
}, hwndOption, streamIdOption, pipeNameOption, encoderOptionsJsonOption);
rootCommand.AddCommand(workerCommand);
```

- [ ] **Step 4: Build CLI**

```bash
dotnet build src/WindowStream.Cli/WindowStream.Cli.csproj -f net8.0-windows10.0.19041.0
```
Expected: PASS. (If `EncoderOptions` is not JSON-deserializable as-is, may need `[JsonInclude]` on private fields or a JSON-friendly options record. Adjust until it builds.)

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Cli
git commit -m "feat(cli): windowstream worker subcommand — single-stream capture+encode pump"
```

### Task 2.4: Real-process worker integration test

Spin up the actual `windowstream worker` binary against a Notepad HWND, drain encoded chunks over a pipe, verify clean shutdown.

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Hosting/WorkerProcessIntegrationTests.cs`

- [ ] **Step 1: Write the integration test**

```csharp
// tests/WindowStream.Integration.Tests/Hosting/WorkerProcessIntegrationTests.cs
#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Hosting;

public class WorkerProcessIntegrationTests
{
    [WindowsFact(Skip = "requires NVIDIA driver; remove Skip locally to run")]
    public async Task WorkerEmitsChunksThroughPipe()
    {
        // Snapshot existing notepad PIDs; kill any new ones in finally (Win11 launcher pattern).
        HashSet<int> existingNotepadPids = NotepadProcessSnapshot.Capture();

        Process notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = false })
            ?? throw new InvalidOperationException("could not start notepad");

        try
        {
            await Task.Delay(500); // give notepad time to display
            long hwnd = NotepadHwndDiscovery.FindNewNotepadHwnd(existingNotepadPids);

            string pipeName = $"windowstream-test-{Guid.NewGuid():N}";
            using NamedPipeServerStream pipeServer = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            EncoderOptions encoderOptions = new EncoderOptions(
                widthPixels: 800, heightPixels: 600,
                framesPerSecond: 30, bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30, safetyKeyframeIntervalSeconds: 1);
            string encoderOptionsJson = JsonSerializer.Serialize(encoderOptions);

            string assembly = typeof(WorkerProcessIntegrationTests).Assembly.Location;
            string repoRoot = TestRootResolver.Resolve(assembly);
            string cliCsproj = System.IO.Path.Combine(repoRoot, "src", "WindowStream.Cli", "WindowStream.Cli.csproj");

            ProcessStartInfo psi = new ProcessStartInfo("dotnet")
            {
                Arguments = $"run --project \"{cliCsproj}\" -f net8.0-windows10.0.19041.0 -- " +
                            $"worker --hwnd {hwnd} --stream-id 1 --pipe-name {pipeName} " +
                            $"--encoder-options {EscapeShellArg(encoderOptionsJson)}",
                UseShellExecute = false,
                RedirectStandardError = true
            };
            using Process worker = Process.Start(psi)
                ?? throw new InvalidOperationException("could not spawn worker");

            try
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await pipeServer.WaitForConnectionAsync(timeout.Token);

                int chunkCount = 0;
                using CancellationTokenSource readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (chunkCount < 5 && !readTimeout.IsCancellationRequested)
                {
                    WorkerChunkFrame frame = await WorkerChunkPipe.ReadChunkAsync(pipeServer, readTimeout.Token);
                    Assert.NotEmpty(frame.Payload);
                    chunkCount++;
                }
                Assert.True(chunkCount >= 5, $"expected ≥5 chunks, got {chunkCount}");

                await WorkerChunkPipe.WriteCommandAsync(pipeServer,
                    new WorkerCommandFrame(WorkerCommandTag.Shutdown), CancellationToken.None);

                bool exited = worker.WaitForExit(5000);
                Assert.True(exited, "worker did not exit within 5s of Shutdown");
                Assert.Equal(0, worker.ExitCode);
            }
            finally
            {
                if (!worker.HasExited)
                {
                    worker.Kill(entireProcessTree: true);
                    worker.WaitForExit(2000);
                }
            }
        }
        finally
        {
            NotepadProcessSnapshot.KillNew(existingNotepadPids);
        }
    }

    private static string EscapeShellArg(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
#endif
```

(`NotepadProcessSnapshot`, `NotepadHwndDiscovery`, `TestRootResolver`, and `WindowsFact` may already exist in `Infrastructure/`. If not, look for similar helpers under `tests/WindowStream.Integration.Tests/Capture/` for a reference pattern; the existing `WgcCaptureSourceSmokeTests.cs` has the PID-snapshot dance.)

- [ ] **Step 2: Run on a machine with NVIDIA + Notepad** (locally; skip on CI without GPU)

```bash
dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter FullyQualifiedName~WorkerProcessIntegrationTests
```
If `Skip` left in place, expected: skipped. Remove the `Skip` argument locally to run; expected: PASS — at least 5 chunks read, worker exits cleanly with code 0.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Hosting
git commit -m "test(integration): worker spawns + emits chunks + clean shutdown via pipe"
```

---

## Phase 3 — Coordinator

### Task 3.1: WindowEnumerator diff/event-emission

Today's `WindowEnumerator` is a one-shot `IEnumerable<WindowInformation> ListWindows()`. We extend it with a `Diff` helper that takes a previous and current snapshot and returns add/remove/update event records, plus a `windowId` allocator.

**Files:**
- Create: `src/WindowStream.Core/Capture/Windows/WindowEnumerationEvent.cs` (sealed-class hierarchy or discriminated record)
- Create: `src/WindowStream.Core/Capture/Windows/WindowIdentityRegistry.cs`
- Modify: `src/WindowStream.Core/Capture/Windows/WindowEnumerator.cs`
- Create: `tests/WindowStream.Core.Tests/Capture/WindowIdentityRegistryTests.cs`

- [ ] **Step 1: Define the event hierarchy + tests**

```csharp
// src/WindowStream.Core/Capture/Windows/WindowEnumerationEvent.cs
namespace WindowStream.Core.Capture.Windows;

public abstract record WindowEnumerationEvent;
public sealed record WindowAppeared(ulong WindowId, WindowInformation Information) : WindowEnumerationEvent;
public sealed record WindowDisappeared(ulong WindowId) : WindowEnumerationEvent;
public sealed record WindowChanged(
    ulong WindowId,
    string? NewTitle,
    int? NewPhysicalWidth,
    int? NewPhysicalHeight) : WindowEnumerationEvent;
```

(Reuses existing `WindowInformation` record from `WindowEnumerator.cs`. If the existing record name differs, adjust accordingly. Verify with `grep -rn "record WindowInformation\|class WindowInformation" src`.)

- [ ] **Step 2: Write WindowIdentityRegistry tests**

```csharp
// tests/WindowStream.Core.Tests/Capture/WindowIdentityRegistryTests.cs
using System.Collections.Generic;
using System.Linq;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public class WindowIdentityRegistryTests
{
    private static WindowInformation Win(long handle, string title, int w = 800, int h = 600)
        => new WindowInformation(new WindowHandle(handle), processId: 1, processName: "test", title: title, physicalWidth: w, physicalHeight: h);

    [Fact]
    public void NewWindow_GetsAppearedEvent_WithFreshId()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        var events = registry.Diff(new[] { Win(0x100, "a") }).ToArray();
        Assert.Single(events);
        WindowAppeared appeared = Assert.IsType<WindowAppeared>(events[0]);
        Assert.Equal(1UL, appeared.WindowId);
    }

    [Fact]
    public void IdsAreMonotonic()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        var events = registry.Diff(new[] { Win(0x100, "a"), Win(0x200, "b") }).ToArray();
        WindowAppeared appeared = Assert.IsType<WindowAppeared>(events.Single());
        Assert.Equal(2UL, appeared.WindowId);
    }

    [Fact]
    public void TitleChange_EmitsWindowChanged_KeepsId()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "old") });
        var events = registry.Diff(new[] { Win(0x100, "new") }).ToArray();
        WindowChanged changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1UL, changed.WindowId);
        Assert.Equal("new", changed.NewTitle);
    }

    [Fact]
    public void DimensionChange_EmitsWindowChanged()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a", 800, 600) });
        var events = registry.Diff(new[] { Win(0x100, "a", 1024, 768) }).ToArray();
        WindowChanged changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1024, changed.NewPhysicalWidth);
        Assert.Equal(768, changed.NewPhysicalHeight);
        Assert.Null(changed.NewTitle);
    }

    [Fact]
    public void HandleGone_EmitsDisappeared()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        var events = registry.Diff(System.Array.Empty<WindowInformation>()).ToArray();
        WindowDisappeared gone = Assert.IsType<WindowDisappeared>(events.Single());
        Assert.Equal(1UL, gone.WindowId);
    }

    [Fact]
    public void ReusedHandle_GetsFreshId()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        registry.Diff(System.Array.Empty<WindowInformation>());
        var events = registry.Diff(new[] { Win(0x100, "b") }).ToArray();
        WindowAppeared appeared = Assert.IsType<WindowAppeared>(events.Single());
        Assert.Equal(2UL, appeared.WindowId); // new id even though hwnd collides
    }

    [Fact]
    public void NoChange_EmitsNoEvents()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        Assert.Empty(registry.Diff(new[] { Win(0x100, "a") }));
    }
}
```

- [ ] **Step 3: Run, verify failure**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WindowIdentityRegistryTests
```
Expected: FAIL.

- [ ] **Step 4: Implement registry**

```csharp
// src/WindowStream.Core/Capture/Windows/WindowIdentityRegistry.cs
using System.Collections.Generic;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Capture.Windows;

public sealed class WindowIdentityRegistry
{
    private readonly Dictionary<long, KnownWindow> handleToKnown = new();
    private ulong nextWindowId = 1;

    public IEnumerable<WindowEnumerationEvent> Diff(IReadOnlyList<WindowInformation> currentSnapshot)
    {
        HashSet<long> seenHandles = new HashSet<long>();
        List<WindowEnumerationEvent> events = new List<WindowEnumerationEvent>();

        foreach (WindowInformation current in currentSnapshot)
        {
            long handle = current.handle.value;
            seenHandles.Add(handle);
            if (handleToKnown.TryGetValue(handle, out KnownWindow? previous))
            {
                bool titleChanged = previous.Title != current.title;
                bool widthChanged = previous.PhysicalWidth != current.physicalWidth;
                bool heightChanged = previous.PhysicalHeight != current.physicalHeight;
                if (titleChanged || widthChanged || heightChanged)
                {
                    events.Add(new WindowChanged(
                        previous.WindowId,
                        titleChanged ? current.title : null,
                        widthChanged ? current.physicalWidth : null,
                        heightChanged ? current.physicalHeight : null));
                    handleToKnown[handle] = previous with
                    {
                        Title = current.title,
                        PhysicalWidth = current.physicalWidth,
                        PhysicalHeight = current.physicalHeight
                    };
                }
            }
            else
            {
                ulong assigned = nextWindowId++;
                handleToKnown[handle] = new KnownWindow(
                    assigned, current.title, current.physicalWidth, current.physicalHeight);
                events.Add(new WindowAppeared(assigned, current));
            }
        }

        List<long> goneHandles = new List<long>();
        foreach (var pair in handleToKnown)
        {
            if (!seenHandles.Contains(pair.Key)) goneHandles.Add(pair.Key);
        }
        foreach (long gone in goneHandles)
        {
            ulong wid = handleToKnown[gone].WindowId;
            handleToKnown.Remove(gone);
            events.Add(new WindowDisappeared(wid));
        }

        return events;
    }

    private sealed record KnownWindow(ulong WindowId, string Title, int PhysicalWidth, int PhysicalHeight);
}
```

(If `WindowInformation` doesn't have `physicalWidth`/`physicalHeight` fields today, extend it minimally to expose those — they come from `GetClientRect`-equivalent logic in `WindowEnumerator`. Adjust based on existing code.)

- [ ] **Step 5: Run, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WindowIdentityRegistryTests
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows/WindowEnumerationEvent.cs src/WindowStream.Core/Capture/Windows/WindowIdentityRegistry.cs tests/WindowStream.Core.Tests/Capture
git commit -m "feat(capture): WindowIdentityRegistry — diff snapshots with stable ids"
```

### Task 3.2: WorkerSupervisor

Manages worker process lifecycle. Tracks active streams, enforces capacity, fires `StreamEnded` when a worker exits.

**Files:**
- Create: `src/WindowStream.Core/Hosting/IWorkerProcessLauncher.cs`
- Create: `src/WindowStream.Core/Hosting/WorkerProcessLauncher.cs` (production impl, `Process.Start`)
- Create: `src/WindowStream.Core/Hosting/StreamHandle.cs` (record)
- Create: `src/WindowStream.Core/Hosting/StreamEndedEventArguments.cs`
- Create: `src/WindowStream.Core/Hosting/WorkerSupervisor.cs`
- Create: `tests/WindowStream.Core.Tests/Hosting/WorkerSupervisorTests.cs`

- [ ] **Step 1: Define the abstraction + supporting types**

```csharp
// src/WindowStream.Core/Hosting/IWorkerProcessLauncher.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public interface IWorkerProcessLauncher
{
    Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken);
}

public interface IWorkerHandle : IAsyncDisposable
{
    Stream Pipe { get; }            // bidirectional stream to talk to the worker
    Task<int> WaitForExitAsync();   // completes with the worker's exit code
    void Kill();                     // best-effort terminate
}

public sealed record WorkerLaunchArguments(
    long Hwnd,
    int StreamId,
    string PipeName,
    string EncoderOptionsJson);
```

```csharp
// src/WindowStream.Core/Hosting/StreamHandle.cs
namespace WindowStream.Core.Hosting;

public sealed record StreamHandle(int StreamId, ulong WindowId);
```

```csharp
// src/WindowStream.Core/Hosting/StreamEndedEventArguments.cs
using System;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Hosting;

public sealed class StreamEndedEventArguments : EventArgs
{
    public StreamEndedEventArguments(int streamId, StreamStoppedReason reason)
    {
        StreamId = streamId;
        Reason = reason;
    }
    public int StreamId { get; }
    public StreamStoppedReason Reason { get; }
}
```

- [ ] **Step 2: Write WorkerSupervisorTests using a fake launcher**

```csharp
// tests/WindowStream.Core.Tests/Hosting/WorkerSupervisorTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerSupervisorTests
{
    private static EncoderOptions DefaultEncoderOptions()
        => new EncoderOptions(800, 600, 30, 4_000_000, 30, 1);

    private sealed class FakeWorkerHandle : IWorkerHandle
    {
        private readonly TaskCompletionSource<int> exitSource = new();
        public FakeWorkerHandle(Stream pipe) { Pipe = pipe; }
        public Stream Pipe { get; }
        public Task<int> WaitForExitAsync() => exitSource.Task;
        public void Kill() => exitSource.TrySetResult(137);
        public ValueTask DisposeAsync() { Kill(); return ValueTask.CompletedTask; }
        public void SimulateClean() => exitSource.TrySetResult(0);
        public void SimulateEncoderFailure() => exitSource.TrySetResult(1);
    }

    private sealed class FakeLauncher : IWorkerProcessLauncher
    {
        public List<FakeWorkerHandle> Launched { get; } = new();
        public Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken ct)
        {
            FakeWorkerHandle handle = new FakeWorkerHandle(new MemoryStream());
            Launched.Add(handle);
            return Task.FromResult<IWorkerHandle>(handle);
        }
    }

    [Fact]
    public async Task StartStream_AssignsMonotonicStreamId()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        StreamHandle a = await supervisor.StartStreamAsync(windowId: 1, hwnd: 0x100, DefaultEncoderOptions(), CancellationToken.None);
        StreamHandle b = await supervisor.StartStreamAsync(windowId: 2, hwnd: 0x200, DefaultEncoderOptions(), CancellationToken.None);
        Assert.Equal(1, a.StreamId);
        Assert.Equal(2, b.StreamId);
    }

    [Fact]
    public async Task StartStream_RefusesPastCapacity()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 1);
        await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        await Assert.ThrowsAsync<EncoderCapacityException>(
            () => supervisor.StartStreamAsync(2, 0x200, DefaultEncoderOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task UnexpectedExit_FiresStreamEnded_WithEncoderFailed()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, args) => ended.TrySetResult(args);

        StreamHandle handle = await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        launcher.Launched[0].SimulateEncoderFailure();

        StreamEndedEventArguments args = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(handle.StreamId, args.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, args.Reason);
    }

    [Fact]
    public async Task CleanExit_FiresStreamEnded_WithClosedByViewer()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, args) => ended.TrySetResult(args);

        await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        launcher.Launched[0].SimulateClean();

        StreamEndedEventArguments args = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamStoppedReason.ClosedByViewer, args.Reason);
    }

    [Fact]
    public async Task StopStream_KillsWorker_FiresEnded()
    {
        FakeLauncher launcher = new FakeLauncher();
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 4);
        TaskCompletionSource<StreamEndedEventArguments> ended = new();
        supervisor.StreamEnded += (_, args) => ended.TrySetResult(args);

        StreamHandle handle = await supervisor.StartStreamAsync(1, 0x100, DefaultEncoderOptions(), CancellationToken.None);
        await supervisor.StopStreamAsync(handle.StreamId);

        StreamEndedEventArguments args = await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(handle.StreamId, args.StreamId);
    }
}
```

- [ ] **Step 3: Implement `WorkerSupervisor` and `EncoderCapacityException`**

```csharp
// src/WindowStream.Core/Hosting/EncoderCapacityException.cs
using System;
namespace WindowStream.Core.Hosting;
public sealed class EncoderCapacityException : Exception
{
    public EncoderCapacityException(int maximum) : base($"server is at NVENC capacity ({maximum} streams)") { }
}
```

```csharp
// src/WindowStream.Core/Hosting/WorkerSupervisor.cs
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Hosting;

public sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly IWorkerProcessLauncher launcher;
    private readonly int maximumConcurrentStreams;
    private readonly ConcurrentDictionary<int, ActiveStream> active = new();
    private int nextStreamId = 0;
    private bool disposed;

    public event EventHandler<StreamEndedEventArguments>? StreamEnded;

    public WorkerSupervisor(IWorkerProcessLauncher launcher, int maximumConcurrentStreams)
    {
        this.launcher = launcher;
        this.maximumConcurrentStreams = maximumConcurrentStreams;
    }

    public async Task<StreamHandle> StartStreamAsync(
        ulong windowId, long hwnd, EncoderOptions encoderOptions, CancellationToken cancellationToken)
    {
        if (active.Count >= maximumConcurrentStreams) throw new EncoderCapacityException(maximumConcurrentStreams);

        int streamId = Interlocked.Increment(ref nextStreamId);
        string pipeName = $"windowstream-{Environment.ProcessId}-{streamId}";

        WorkerLaunchArguments launchArgs = new WorkerLaunchArguments(
            hwnd, streamId, pipeName,
            JsonSerializer.Serialize(encoderOptions));
        IWorkerHandle handle = await launcher.LaunchAsync(launchArgs, cancellationToken).ConfigureAwait(false);

        ActiveStream record = new ActiveStream(streamId, windowId, handle, ExpectedExit.Unset);
        active[streamId] = record;

        _ = MonitorAsync(record);
        return new StreamHandle(streamId, windowId);
    }

    public async Task StopStreamAsync(int streamId)
    {
        if (!active.TryGetValue(streamId, out ActiveStream? record)) return;
        record.Expected = ExpectedExit.ClosedByViewer;
        record.Handle.Kill();
        await record.Handle.DisposeAsync().ConfigureAwait(false);
    }

    public Stream? GetPipe(int streamId)
        => active.TryGetValue(streamId, out ActiveStream? record) ? record.Handle.Pipe : null;

    private async Task MonitorAsync(ActiveStream record)
    {
        int exitCode = await record.Handle.WaitForExitAsync().ConfigureAwait(false);
        active.TryRemove(record.StreamId, out _);
        StreamStoppedReason reason = record.Expected switch
        {
            ExpectedExit.ClosedByViewer => StreamStoppedReason.ClosedByViewer,
            _ => exitCode switch
            {
                0 => StreamStoppedReason.ClosedByViewer,
                1 => StreamStoppedReason.EncoderFailed,
                2 => StreamStoppedReason.CaptureFailed,
                _ => StreamStoppedReason.EncoderFailed
            }
        };
        StreamEnded?.Invoke(this, new StreamEndedEventArguments(record.StreamId, reason));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        foreach (var pair in active)
        {
            pair.Value.Expected = ExpectedExit.ClosedByViewer;
            pair.Value.Handle.Kill();
            await pair.Value.Handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private enum ExpectedExit { Unset, ClosedByViewer }
    private sealed class ActiveStream
    {
        public int StreamId { get; }
        public ulong WindowId { get; }
        public IWorkerHandle Handle { get; }
        public ExpectedExit Expected { get; set; }
        public ActiveStream(int s, ulong w, IWorkerHandle h, ExpectedExit e) { StreamId = s; WindowId = w; Handle = h; Expected = e; }
    }
}
```

(Note: `Stream` import — add `using System.IO;` to file. The `GetPipe` accessor is used by `StreamRouter` in Task 3.3.)

- [ ] **Step 4: Run, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~WorkerSupervisorTests
```
Expected: PASS.

- [ ] **Step 5: Implement production `WorkerProcessLauncher`**

```csharp
// src/WindowStream.Core/Hosting/WorkerProcessLauncher.cs
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public sealed class WorkerProcessLauncher : IWorkerProcessLauncher
{
    private readonly string executablePath;

    public WorkerProcessLauncher(string executablePath) { this.executablePath = executablePath; }

    public async Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments args, CancellationToken cancellationToken)
    {
        NamedPipeServerStream pipe = new NamedPipeServerStream(
            args.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = executablePath,
            ArgumentList =
            {
                "worker",
                "--hwnd", args.Hwnd.ToString(),
                "--stream-id", args.StreamId.ToString(),
                "--pipe-name", args.PipeName,
                "--encoder-options", args.EncoderOptionsJson
            },
            UseShellExecute = false,
            RedirectStandardError = true
        };
        Process process = Process.Start(psi) ?? throw new InvalidOperationException("worker spawn failed");
        try
        {
            using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            await pipe.DisposeAsync();
            throw;
        }
        return new WorkerHandle(process, pipe);
    }

    private sealed class WorkerHandle : IWorkerHandle
    {
        private readonly Process process;
        public WorkerHandle(Process process, NamedPipeServerStream pipe) { this.process = process; Pipe = pipe; }
        public Stream Pipe { get; }
        public Task<int> WaitForExitAsync()
        {
            TaskCompletionSource<int> source = new TaskCompletionSource<int>();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => source.TrySetResult(process.ExitCode);
            if (process.HasExited) source.TrySetResult(process.ExitCode);
            return source.Task;
        }
        public void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
        public async ValueTask DisposeAsync()
        {
            Kill();
            try { await ((NamedPipeServerStream)Pipe).DisposeAsync(); } catch { }
            process.Dispose();
        }
    }
}
```

- [ ] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Hosting tests/WindowStream.Core.Tests/Hosting/WorkerSupervisorTests.cs
git commit -m "feat(hosting): WorkerSupervisor + WorkerProcessLauncher"
```

### Task 3.3: StreamRouter

Reads chunks from per-stream pipes, tags with `streamId`, hands to a fragmenter→UDP send pipeline.

**Files:**
- Create: `src/WindowStream.Core/Hosting/StreamRouter.cs`
- Create: `tests/WindowStream.Core.Tests/Hosting/StreamRouterTests.cs`

- [ ] **Step 1: Tests**

```csharp
// tests/WindowStream.Core.Tests/Hosting/StreamRouterTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class StreamRouterTests
{
    [Fact]
    public async Task RoutesChunksFromPipe_TaggedWithStreamId()
    {
        Channel<TaggedChunk> output = Channel.CreateUnbounded<TaggedChunk>();
        StreamRouter router = new StreamRouter(output);

        MemoryStream pipe = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(pipe,
            new WorkerChunkFrame(100UL, true, new byte[] { 0xAA }), CancellationToken.None);
        await WorkerChunkPipe.WriteChunkAsync(pipe,
            new WorkerChunkFrame(200UL, false, new byte[] { 0xBB }), CancellationToken.None);
        pipe.Position = 0;

        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task readerTask = router.ReadFromPipeAsync(streamId: 7, pipe, cancellation.Token);

        TaggedChunk first = await output.Reader.ReadAsync(cancellation.Token);
        Assert.Equal(7, first.StreamId);
        Assert.Equal(100UL, first.Frame.PresentationTimestampMicroseconds);
        Assert.True(first.Frame.IsKeyframe);

        TaggedChunk second = await output.Reader.ReadAsync(cancellation.Token);
        Assert.Equal(7, second.StreamId);
        Assert.False(second.Frame.IsKeyframe);

        cancellation.Cancel();
        try { await readerTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PipeClosed_StopsReader_DoesNotThrow()
    {
        Channel<TaggedChunk> output = Channel.CreateUnbounded<TaggedChunk>();
        StreamRouter router = new StreamRouter(output);
        MemoryStream emptyPipe = new MemoryStream();
        await router.ReadFromPipeAsync(streamId: 1, emptyPipe, CancellationToken.None);
        Assert.False(output.Reader.TryRead(out _));
    }
}
```

- [ ] **Step 2: Run, verify failure**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~StreamRouterTests
```

- [ ] **Step 3: Implement**

```csharp
// src/WindowStream.Core/Hosting/StreamRouter.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public sealed record TaggedChunk(int StreamId, WorkerChunkFrame Frame);

public sealed class StreamRouter
{
    private readonly Channel<TaggedChunk> sink;

    public StreamRouter(Channel<TaggedChunk> sink) { this.sink = sink; }

    public async Task ReadFromPipeAsync(int streamId, Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WorkerChunkFrame frame = await WorkerChunkPipe.ReadChunkAsync(pipe, cancellationToken).ConfigureAwait(false);
                await sink.Writer.WriteAsync(new TaggedChunk(streamId, frame), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) { /* worker pipe closed — normal stop */ }
        catch (OperationCanceledException) { /* normal */ }
    }
}
```

- [ ] **Step 4: Run, verify pass**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj --filter FullyQualifiedName~StreamRouterTests
```

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Hosting/StreamRouter.cs tests/WindowStream.Core.Tests/Hosting/StreamRouterTests.cs
git commit -m "feat(hosting): StreamRouter — pipe reader → tagged-chunk channel"
```

### Task 3.4: LoadShedder

Sits between `StreamRouter` and `NalFragmenter`. Drops oldest non-keyframe chunks per stream when queue depth exceeds threshold.

**Files:**
- Create: `src/WindowStream.Core/Hosting/LoadShedder.cs`
- Create: `tests/WindowStream.Core.Tests/Hosting/LoadShedderTests.cs`

- [ ] **Step 1: Tests**

```csharp
// tests/WindowStream.Core.Tests/Hosting/LoadShedderTests.cs
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class LoadShedderTests
{
    private static TaggedChunk Chunk(int streamId, ulong pts, bool keyframe = false)
        => new TaggedChunk(streamId, new WorkerChunkFrame(pts, keyframe, new byte[] { 0xFF }));

    [Fact]
    public async Task UnderThreshold_PassesAllChunks()
    {
        Channel<TaggedChunk> input = Channel.CreateUnbounded<TaggedChunk>();
        Channel<TaggedChunk> output = Channel.CreateUnbounded<TaggedChunk>();
        LoadShedder shedder = new LoadShedder(input, output, perStreamMaximumQueueDepth: 4);

        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task task = shedder.RunAsync(cancellation.Token);

        await input.Writer.WriteAsync(Chunk(1, 100));
        await input.Writer.WriteAsync(Chunk(1, 200));
        await input.Writer.WriteAsync(Chunk(1, 300));

        Assert.Equal(100UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);
        Assert.Equal(200UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);
        Assert.Equal(300UL, (await output.Reader.ReadAsync()).Frame.PresentationTimestampMicroseconds);

        cancellation.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    }

    // Threshold-trip behavior is implementation-detail; the spec leaves
    // concrete numbers + signal mechanism to implementation. For v2.0 we
    // implement a simple backpressure: if `output` channel write blocks for
    // longer than a threshold, drop the next non-keyframe input. Test the
    // KEYFRAME-NEVER-DROPPED invariant explicitly.
    [Fact]
    public async Task KeyframesAreNeverDropped()
    {
        // Bounded output of size 1 + producer that blocks until consumer drains.
        Channel<TaggedChunk> input = Channel.CreateUnbounded<TaggedChunk>();
        Channel<TaggedChunk> output = Channel.CreateBounded<TaggedChunk>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        LoadShedder shedder = new LoadShedder(input, output, perStreamMaximumQueueDepth: 1);
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task task = shedder.RunAsync(cancellation.Token);

        await input.Writer.WriteAsync(Chunk(1, 100, keyframe: false)); // fills output
        await input.Writer.WriteAsync(Chunk(1, 200, keyframe: false)); // queued internally
        await input.Writer.WriteAsync(Chunk(1, 300, keyframe: true));  // keyframe — must survive

        await Task.Delay(100);
        // Drain: read 2 chunks (one of the non-keyframes was dropped, keyframe survives)
        TaggedChunk first = await output.Reader.ReadAsync();
        TaggedChunk second = await output.Reader.ReadAsync();

        Assert.True(first.Frame.IsKeyframe || second.Frame.IsKeyframe,
            "keyframe (pts=300) must appear in output");

        cancellation.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    }
}
```

- [ ] **Step 2: Run, verify failure**

- [ ] **Step 3: Implement (simple per-stream queue with keyframe-priority drop)**

```csharp
// src/WindowStream.Core/Hosting/LoadShedder.cs
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public sealed class LoadShedder
{
    private readonly Channel<TaggedChunk> input;
    private readonly Channel<TaggedChunk> output;
    private readonly int perStreamMaximumQueueDepth;

    public LoadShedder(Channel<TaggedChunk> input, Channel<TaggedChunk> output, int perStreamMaximumQueueDepth)
    {
        this.input = input;
        this.output = output;
        this.perStreamMaximumQueueDepth = perStreamMaximumQueueDepth;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Dictionary<int, Queue<TaggedChunk>> perStreamQueues = new();
        await foreach (TaggedChunk chunk in input.Reader.ReadAllAsync(cancellationToken))
        {
            if (!perStreamQueues.TryGetValue(chunk.StreamId, out Queue<TaggedChunk>? queue))
            {
                queue = new Queue<TaggedChunk>();
                perStreamQueues[chunk.StreamId] = queue;
            }
            queue.Enqueue(chunk);

            // Drop oldest non-keyframes until under threshold.
            while (queue.Count > perStreamMaximumQueueDepth)
            {
                TaggedChunk oldest = queue.Peek();
                if (oldest.Frame.IsKeyframe)
                {
                    // Walk forward and find the oldest non-keyframe to drop instead.
                    bool dropped = false;
                    Queue<TaggedChunk> rebuilt = new Queue<TaggedChunk>();
                    foreach (TaggedChunk q in queue)
                    {
                        if (!dropped && !q.Frame.IsKeyframe) { dropped = true; continue; }
                        rebuilt.Enqueue(q);
                    }
                    if (!dropped) break; // queue is all keyframes — leave it; pressure will resolve via output blocking
                    perStreamQueues[chunk.StreamId] = rebuilt;
                    queue = rebuilt;
                }
                else
                {
                    queue.Dequeue();
                }
            }

            // Try to push the head non-blockingly.
            while (queue.Count > 0 && output.Writer.TryWrite(queue.Peek()))
            {
                queue.Dequeue();
            }
        }
    }
}
```

- [ ] **Step 4: Run, verify pass**

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Hosting/LoadShedder.cs tests/WindowStream.Core.Tests/Hosting/LoadShedderTests.cs
git commit -m "feat(hosting): LoadShedder — drop non-keyframes under per-stream queue pressure"
```

### Task 3.5: FocusRelay + IForegroundWindowApi

**Files:**
- Create: `src/WindowStream.Core/Session/Input/IForegroundWindowApi.cs`
- Create: `src/WindowStream.Core/Session/Input/ForegroundWindowApi.cs` (Windows-only)
- Create: `src/WindowStream.Core/Session/Input/FocusRelay.cs`
- Create: `tests/WindowStream.Core.Tests/Session/Input/FocusRelayTests.cs`

- [ ] **Step 1: Define abstraction + tests**

```csharp
// src/WindowStream.Core/Session/Input/IForegroundWindowApi.cs
namespace WindowStream.Core.Session.Input;

public interface IForegroundWindowApi
{
    long GetForegroundWindow();
    uint GetWindowThreadProcessId(long hwnd);
    bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);
    bool SetForegroundWindow(long hwnd);
    uint CurrentThreadId();
}
```

```csharp
// tests/WindowStream.Core.Tests/Session/Input/FocusRelayTests.cs
using System.Collections.Generic;
using WindowStream.Core.Session.Input;
using Xunit;

namespace WindowStream.Core.Tests.Session.Input;

public class FocusRelayTests
{
    private sealed class FakeForegroundApi : IForegroundWindowApi
    {
        public long Foreground { get; set; }
        public Dictionary<long, uint> HandleToThread { get; } = new();
        public List<string> Events { get; } = new();

        public long GetForegroundWindow() => Foreground;
        public uint GetWindowThreadProcessId(long hwnd) => HandleToThread.TryGetValue(hwnd, out uint t) ? t : 0;
        public bool AttachThreadInput(uint source, uint target, bool attach)
        {
            Events.Add(attach ? $"attach({source}->{target})" : $"detach({source}->{target})");
            return true;
        }
        public bool SetForegroundWindow(long hwnd)
        {
            Events.Add($"setForeground({hwnd})");
            Foreground = hwnd;
            return true;
        }
        public uint CurrentThreadId() => 99;
    }

    [Fact]
    public void BringToForeground_RunsAttachDetachDance()
    {
        FakeForegroundApi api = new FakeForegroundApi
        {
            Foreground = 0x100,
            HandleToThread = { [0x100] = 10, [0x200] = 20 }
        };
        FocusRelay relay = new FocusRelay(api);

        relay.BringToForeground(0x200);

        Assert.Equal(new[]
        {
            "attach(99->10)",   // attach to current foreground's thread (0x100 → tid 10)
            "setForeground(512)",
            "detach(99->10)"
        }, api.Events.ToArray());
    }

    [Fact]
    public void BringToForeground_NoOpIfAlreadyForeground()
    {
        FakeForegroundApi api = new FakeForegroundApi { Foreground = 0x100, HandleToThread = { [0x100] = 10 } };
        FocusRelay relay = new FocusRelay(api);
        relay.BringToForeground(0x100);
        Assert.Empty(api.Events);
    }
}
```

- [ ] **Step 2: Run, verify failure**

- [ ] **Step 3: Implement**

```csharp
// src/WindowStream.Core/Session/Input/FocusRelay.cs
namespace WindowStream.Core.Session.Input;

public sealed class FocusRelay
{
    private readonly IForegroundWindowApi api;

    public FocusRelay(IForegroundWindowApi api) { this.api = api; }

    public bool BringToForeground(long hwnd)
    {
        long currentForeground = api.GetForegroundWindow();
        if (currentForeground == hwnd) return true;
        uint currentThread = api.GetWindowThreadProcessId(currentForeground);
        uint myThread = api.CurrentThreadId();
        api.AttachThreadInput(myThread, currentThread, true);
        try
        {
            return api.SetForegroundWindow(hwnd);
        }
        finally
        {
            api.AttachThreadInput(myThread, currentThread, false);
        }
    }
}
```

```csharp
// src/WindowStream.Core/Session/Input/ForegroundWindowApi.cs
#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace WindowStream.Core.Session.Input;

public sealed class ForegroundWindowApi : IForegroundWindowApi
{
    public long GetForegroundWindow() => GetForegroundWindowNative().ToInt64();

    public uint GetWindowThreadProcessId(long hwnd) =>
        GetWindowThreadProcessIdNative(new IntPtr(hwnd), out _);

    public bool AttachThreadInput(uint source, uint target, bool attach) =>
        AttachThreadInputNative(source, target, attach);

    public bool SetForegroundWindow(long hwnd) =>
        SetForegroundWindowNative(new IntPtr(hwnd));

    public uint CurrentThreadId() => GetCurrentThreadIdNative();

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdNative(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInputNative(uint sourceThreadId, uint targetThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(IntPtr hwnd);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();
}
#endif
```

- [ ] **Step 4: Run, verify pass**

- [ ] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Session/Input tests/WindowStream.Core.Tests/Session/Input
git commit -m "feat(input): FocusRelay with AttachThreadInput dance"
```

### Task 3.6: CoordinatorControlServer — TCP control + viewer endpoint + per-stream control routing

This is the heart of the coordinator. Replaces the accept loop, ServeViewerAsync, RunViewerReceiveLoopAsync, RunHeartbeatAsync from `SessionHost`.

**Files:**
- Create: `src/WindowStream.Core/Session/CoordinatorControlServer.cs`
- Create: `src/WindowStream.Core/Hosting/CoordinatorOptions.cs`
- Create: `tests/WindowStream.Core.Tests/Session/CoordinatorControlServerTests.cs`

This task is the largest in the plan; tests focus on the logic that was previously hidden inside SessionHost. The test surface uses the existing `IControlChannel` abstraction (already mockable) and a fake `WorkerSupervisor` substitute.

- [ ] **Step 1: Define `CoordinatorOptions`**

```csharp
// src/WindowStream.Core/Hosting/CoordinatorOptions.cs
namespace WindowStream.Core.Hosting;

public sealed record CoordinatorOptions(
    int HeartbeatIntervalMilliseconds,
    int HeartbeatTimeoutMilliseconds,
    int ServerVersion,
    int MaximumConcurrentStreams);
```

- [ ] **Step 2: Sketch the public surface (TDD)**

The server exposes:
- `Task RunAsync(IPEndPoint udpLocalEndpoint, int tcpPort, CancellationToken cancellationToken)`
- Internal events: `HelloReceived`, `OpenStreamRequested`, `CloseStreamRequested`, `PauseRequested`, `ResumeRequested`, `FocusRequested`, `KeyEventRequested` — but for v2.0 these wire through directly to the supervisor + focus relay + worker pipes.

Constructor takes:
- `CoordinatorOptions options`
- `ITcpConnectionAcceptor tcpAcceptor`
- `IUdpVideoSender udpSender`
- `WorkerSupervisor supervisor`
- `WindowIdentityRegistry identityRegistry` — current state of the window enumeration
- `Func<IEnumerable<WindowDescriptor>> getCurrentWindows` — for SERVER_HELLO + LIST_WINDOWS
- `Action<int, WorkerCommandFrame> sendWorkerCommand` — coordinator→worker control delegate (resolves streamId → worker pipe)
- `FocusRelay focusRelay`
- `Win32InputInjector inputInjector` (or test-substitutable abstraction; existing class lives in `Session/Input/`)
- `TimeProvider timeProvider`

Add an event hook for `WindowEnumerationEvent`s: server subscribes and emits `WINDOW_ADDED`/`WINDOW_REMOVED`/`WINDOW_UPDATED` over the active control channel.

- [ ] **Step 3: Write CoordinatorControlServerTests** (focus on routing logic, not transport)

```csharp
// tests/WindowStream.Core.Tests/Session/CoordinatorControlServerTests.cs
//
// Use the existing IControlChannel test fixtures from
// tests/WindowStream.Core.Tests/Session/ — there are mock implementations
// from the SessionHost test era that can be repurposed. If they need
// extending to v2 messages, do so here.
//
// Test cases:
// - HELLO triggers SERVER_HELLO with current windows snapshot
// - WINDOW_ADDED enumeration event → WindowAddedMessage on channel
// - WINDOW_REMOVED enumeration event → WindowRemovedMessage on channel
// - OPEN_STREAM at capacity → ERROR{ENCODER_CAPACITY}
// - OPEN_STREAM with unknown windowId → ERROR{WINDOW_NOT_FOUND}
// - OPEN_STREAM happy path → spawns worker via fake supervisor + sends STREAM_STARTED
// - CLOSE_STREAM → calls supervisor.StopStreamAsync
// - PAUSE_STREAM / RESUME_STREAM → coordinator→worker WorkerCommandTag.Pause / .Resume
// - REQUEST_KEYFRAME → coordinator→worker RequestKeyframe
// - FOCUS_WINDOW → calls focusRelay.BringToForeground with the right HWND
// - KEY_EVENT → routes to focusRelay (re-focus if needed) + Win32InputInjector
// - StreamEnded event from supervisor → STREAM_STOPPED with that reason
// - Second viewer connecting → ERROR{VIEWER_BUSY} on the second channel; first remains active
//
// Each test sets up a fake `ITcpConnectionAcceptor` that yields a controllable
// channel, runs the server, drives messages, asserts outputs.
```

(Concrete code for these tests follows the `IControlChannel` mock pattern already used in `tests/WindowStream.Core.Tests/Session/`. Write each test as a `[Fact]` with the structure above. The implementing engineer should look at `tests/WindowStream.Core.Tests/Session/SessionHostTests.cs` — if present — for the mock conventions, or create a `FakeControlChannel.cs` helper that records sends and exposes a `QueueIncoming` method.)

- [ ] **Step 4: Run tests, expect failure**

- [ ] **Step 5: Implement `CoordinatorControlServer`**

The implementation pattern mirrors today's `SessionHost.RunAcceptLoopAsync` + `ServeViewerAsync` + `RunViewerReceiveLoopAsync` + `RunHeartbeatAsync`, with these differences:

- `BuildActiveStreamDescriptor` retires; `SERVER_HELLO` carries `(serverVersion, udpPort, windows[])`.
- Each `OpenStreamMessage` calls `supervisor.StartStreamAsync(...)` after probing dimensions; on success sends `STREAM_STARTED{streamId, windowId, codec, width, height, framesPerSecond}`. On `EncoderCapacityException` sends `ERROR{ENCODER_CAPACITY}`.
- `supervisor.StreamEnded` event triggers `STREAM_STOPPED{streamId, reason}` on the active channel.
- Pause/Resume/RequestKeyframe → `sendWorkerCommand(streamId, ...)`.
- KeyEvent → `focusRelay.BringToForeground(hwndForStreamId)` if foreground mismatch, then `Win32InputInjector.InjectKey(...)`.
- WindowEnumerationEvent → push corresponding WINDOW_* message.

(Full implementation will be ~250 lines; produce it iteratively driven by the test set.)

- [ ] **Step 6: Run tests, expect pass**

- [ ] **Step 7: Commit**

```bash
git add src/WindowStream.Core/Session/CoordinatorControlServer.cs src/WindowStream.Core/Hosting/CoordinatorOptions.cs tests/WindowStream.Core.Tests/Session/CoordinatorControlServerTests.cs
git commit -m "feat(session): CoordinatorControlServer — v2 single-viewer multi-stream control"
```

### Task 3.7: CoordinatorLauncher (CLI wiring) + retire SessionHost

Wires every coordinator component together for the production `windowstream serve` command. Probes WGC dimensions on `OPEN_STREAM` (logic moves out of `SessionHostLauncherAdapter`).

**Files:**
- Create: `src/WindowStream.Cli/Hosting/CoordinatorLauncher.cs` (Windows-only)
- Modify: `src/WindowStream.Cli/Commands/ServeCommandHandler.cs` (drop --hwnd / --title-matches; new behavior is "spin up coordinator and wait")
- Delete: `src/WindowStream.Cli/Hosting/SessionHostLauncherAdapter.cs`
- Delete: `src/WindowStream.Core/Session/SessionHost.cs`
- Delete: `src/WindowStream.Core/Session/SessionHostOptions.cs`
- Modify: any other consumers of those types

- [ ] **Step 1: Sketch CoordinatorLauncher**

```csharp
// src/WindowStream.Cli/Hosting/CoordinatorLauncher.cs
#if WINDOWS
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Discovery;
using WindowStream.Core.Hosting;
using WindowStream.Core.Session;
using WindowStream.Core.Session.Adapters;
using WindowStream.Core.Session.Input;

namespace WindowStream.Cli.Hosting;

public sealed class CoordinatorLauncher : ISessionHostLauncher
{
    private readonly int tcpPort;
    private readonly TextWriter output;

    public CoordinatorLauncher(int tcpPort, TextWriter output)
    {
        this.tcpPort = tcpPort;
        this.output = output;
    }

    // NOTE: ISessionHostLauncher's existing signature took a single HWND.
    // For v2 we change `ServeCommandHandler` to call a new entry point
    // `RunAsync()` with no HWND. Update or replace the interface.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 1. Build window enumerator + identity registry
        WindowEnumerator enumerator = new WindowEnumerator(...);
        WindowIdentityRegistry registry = new WindowIdentityRegistry();

        // 2. Build supervisor with production launcher
        string executablePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        WorkerProcessLauncher launcher = new WorkerProcessLauncher(executablePath);
        WorkerSupervisor supervisor = new WorkerSupervisor(launcher, maximumConcurrentStreams: 8);

        // 3. Build router + load shedder + UDP send pipeline
        Channel<TaggedChunk> routerOutput = Channel.CreateUnbounded<TaggedChunk>();
        Channel<TaggedChunk> shedderOutput = Channel.CreateUnbounded<TaggedChunk>();
        StreamRouter router = new StreamRouter(routerOutput);
        LoadShedder shedder = new LoadShedder(routerOutput, shedderOutput, perStreamMaximumQueueDepth: 8);

        UdpVideoSenderAdapter udpSender = new UdpVideoSenderAdapter();
        await udpSender.BindAsync(new IPEndPoint(IPAddress.Any, 0), cancellationToken);

        TcpConnectionAcceptorAdapter tcpAcceptor = new TcpConnectionAcceptorAdapter(TimeProvider.System);

        // 4. Build coordinator control server
        ForegroundWindowApi foregroundApi = new ForegroundWindowApi();
        FocusRelay focusRelay = new FocusRelay(foregroundApi);

        CoordinatorOptions coordinatorOptions = new CoordinatorOptions(
            HeartbeatIntervalMilliseconds: 2000,
            HeartbeatTimeoutMilliseconds: 10000,
            ServerVersion: 2,
            MaximumConcurrentStreams: 8);

        CoordinatorControlServer controlServer = new CoordinatorControlServer(
            options: coordinatorOptions,
            tcpAcceptor: tcpAcceptor,
            udpSender: udpSender,
            supervisor: supervisor,
            registry: registry,
            // ... + worker-command sender + focus relay + input injector + time provider
            timeProvider: TimeProvider.System);

        // 5. Start enumerator timer
        // 6. Start fragmenter loop reading shedderOutput → udpSender (needs viewer endpoint from controlServer)
        // 7. Advertise on mDNS
        // 8. Run controlServer.RunAsync until cancellation

        AdvertisementOptions advertisementOptions = new AdvertisementOptions(
            hostname: Environment.MachineName,
            protocolMajorVersion: 2,
            protocolRevision: 0);
        await using ServerAdvertiser advertiser = new ServerAdvertiser(new MakaretuMulticastServiceHost());
        await advertiser.StartAsync(advertisementOptions, controlServer.TcpPort, cancellationToken);

        await controlServer.RunAsync(new IPEndPoint(IPAddress.Any, 0), tcpPort, cancellationToken);
    }
}
#endif
```

(The CoordinatorControlServer `RunAsync` and constructor signature need finalizing during Task 3.6 — make them match.)

- [ ] **Step 2: Update `ServeCommandHandler`** to call `CoordinatorLauncher.RunAsync` (no HWND, no title pattern). Drop `--hwnd` and `--title-matches` from `ServeArguments`.

- [ ] **Step 3: Update `RootCommandBuilder`** to remove the dropped options.

- [ ] **Step 4: Delete `SessionHost.cs`, `SessionHostOptions.cs`, `SessionHostLauncherAdapter.cs`** — their consumers should now route through CoordinatorLauncher.

- [ ] **Step 5: `dotnet build` the entire solution**

```bash
dotnet build WindowStream.sln
```
Expected: PASS.

- [ ] **Step 6: `dotnet test` Core tests**

```bash
dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
```
Expected: PASS — including 100% line+branch coverage gate.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(cli): CoordinatorLauncher replaces SessionHostLauncherAdapter; SessionHost retires"
```

---

## Phase 4 — End-to-end .NET integration

Goal: an integration test harness that exercises the full coordinator + worker stack against a fake viewer, with concrete assertions for every multi-stream behavior.

### Task 4.1: CoordinatorLoopbackHarness

Replaces the v1 `SessionHostLoopbackHarness` (deleted in Task 1.6).

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Loopback/CoordinatorLoopbackHarness.cs`

The harness:
- Spawns the coordinator in-process (or as a child process — pick in-process for test speed).
- Provides a `FakeViewer` that:
  - Connects TCP to the coordinator's listener.
  - Speaks the v2 protocol (HELLO, VIEWER_READY, OPEN_STREAM, ...).
  - Receives push events (WindowAdded, StreamStarted, StreamStopped, ...).
  - Binds a UDP receiver and reassembles fragments by `streamId`.
  - Exposes per-streamId frame counts + last decoded frame for assertions.
- Hides the boilerplate (TCP framing, UDP packet parsing, fragment reassembly).

(Implementation pattern follows `SessionHostLoopbackHarness.cs` from before its deletion — pull from git history if needed: `git show 33d60f5^:tests/WindowStream.Integration.Tests/Loopback/SessionHostLoopbackHarness.cs`. Adapt for v2 messages and multi-stream demultiplex.)

- [ ] **Step 1: Build the harness**

(See note above — a standalone harness with no test assertions. Goal: compiles + can be used by Task 4.2's tests.)

- [ ] **Step 2: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Loopback/CoordinatorLoopbackHarness.cs
git commit -m "test(integration): CoordinatorLoopbackHarness — multi-stream fake viewer"
```

### Task 4.2-4.7: End-to-end behavioral tests

Each task is one `[Fact]` driven by the harness:

- **Task 4.2: open 2 streams, both render** — opens two windows (real WGC against two notepads OR a synthetic capture source), waits for ≥10 frames per streamId, asserts non-black decoded content.
- **Task 4.3: close one stream, other unaffected** — opens 2, closes streamId 1, asserts streamId 2 keeps producing frames AND `STREAM_STOPPED{ClosedByViewer}` arrives for streamId 1.
- **Task 4.4: kill worker, sibling unaffected** — opens 2, kills worker for streamId 1 via `Process.Kill`, asserts `STREAM_STOPPED{EncoderFailed}` arrives, asserts streamId 2 keeps producing.
- **Task 4.5: NVENC capacity rejection** — instantiate coordinator with `MaximumConcurrentStreams=1`, open one, attempt second, assert `ERROR{ENCODER_CAPACITY}`.
- **Task 4.6: pause + resume + keyframe** — open stream, send PAUSE, assert no UDP traffic for 2s, send RESUME, assert next packet has IDR flag bit 0 set.
- **Task 4.7: focus relay** — open two notepads, send FOCUS_WINDOW for the second, send KEY_EVENT('A'), use Win32 to read the second notepad's title bar (or text content) and assert the 'A' landed there. (May need a small probe utility; alternative: use `GetForegroundWindow` to assert which HWND is foreground after FOCUS_WINDOW.)

For each:
- [ ] Step 1: write the [Fact]
- [ ] Step 2: run, verify failure
- [ ] Step 3: implement what's missing in coordinator if any
- [ ] Step 4: verify pass
- [ ] Step 5: commit

Commit messages:
- `test(integration): two streams render simultaneously`
- `test(integration): closing one stream leaves siblings unaffected`
- `test(integration): worker death emits STREAM_STOPPED{EncoderFailed} without affecting siblings`
- `test(integration): coordinator refuses past NVENC capacity`
- `test(integration): pause halts traffic; resume forces keyframe`
- `test(integration): FOCUS_WINDOW + KEY_EVENT routes input to selected window`

After Task 4.7:

```bash
dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj
```
Expected: PASS (locally on a Windows + NVIDIA box; tests with `Skip` annotations skip on CI).

---

## Phase 5 — Viewer migration (Kotlin)

Goal: portable flavor end-to-end on the v2 protocol with a one-server-many-windows picker and a panel switcher replacing the grid.

(Note: GXR multi-panel is out of scope per spec.)

### Task 5.1: New protocol message types in viewer

Mirror the .NET protocol additions in Kotlin.

**Files:**
- Modify: `viewer/.../control/ProtocolMessages.kt` — add new sealed-class arms for `WindowAdded`, `WindowRemoved`, `WindowUpdated`, `WindowSnapshot`, `ListWindows`, `OpenStream`, `CloseStream`, `PauseStream`, `ResumeStream`, `FocusWindow`. Refactor `ServerHello` (replace `activeStream` with `udpPort` + `windows`), `StreamStarted` (add `windowId`), `StreamStopped` (add `reason`), `ViewerReady` (drop `streamId`), `KeyEvent` (add `streamId`). Add `WindowDescriptor` data class.
- Modify: `viewer/.../control/ProtocolSerialization.kt` — add `@SerialName` annotations for each new discriminator.
- Add: `StreamStoppedReason` enum.
- Tests: write equivalent round-trip JUnit tests in `viewer/.../control/ProtocolSerializationTest.kt` (pattern existing test already covers v1 message round-trip).

- [ ] Step 1: write failing tests for each new message
- [ ] Step 2: implement the data classes
- [ ] Step 3: register polymorphism in the JSON setup
- [ ] Step 4: run `./gradlew :app:test` — expect PASS
- [ ] Step 5: commit `feat(viewer/protocol): v2 message types`

### Task 5.2: EncodedFrame carries streamId; multiplex receiver

The packet header already carries `streamId`. Today's `UdpTransportReceiver` ignores it. Update the flow type and the consumer.

**Files:**
- Modify: `viewer/.../transport/EncodedFrame.kt` — add `streamId: Int`.
- Modify: `viewer/.../transport/PacketHeader.kt` — verify the header parser exposes `streamId`.
- Modify: `viewer/.../transport/FragmentReassembler.kt` — key reassembly buffer by `(streamId, sequence)` instead of `sequence` alone.
- Modify: `viewer/.../transport/UdpTransportReceiver.kt` — emit `EncodedFrame` with `streamId` populated from the header.
- Create: `viewer/.../transport/StreamMultiplexer.kt` — given a `Flow<EncodedFrame>`, return `Map<Int, Flow<EncodedFrame>>` (or a function `getFlow(streamId): Flow<EncodedFrame>`).
- Tests: feed synthetic packets with mixed streamIds, assert correct demultiplex.

- [ ] Step 1: write failing test (multiplex demux)
- [ ] Step 2: implement
- [ ] Step 3: pass
- [ ] Step 4: commit `feat(viewer/transport): multiplex EncodedFrame flow by streamId`

### Task 5.3: MultiStreamControlClient

Replaces `ControlClient` to support multiple parallel `OPEN_STREAM` flows over a single TCP connection.

**Files:**
- Create: `viewer/.../control/MultiStreamControlClient.kt`
- API:
  - `connect(host, port): MultiStreamControlConnection`
  - `MultiStreamControlConnection`:
    - `incoming: Flow<ControlMessage>` (everything pushed by server)
    - `send(message: ControlMessage)`
    - `openStream(windowId: ULong): Flow<StreamLifecycleEvent>` — returns events tied to the resulting streamId

- Modify: `viewer/.../app/MultiStreamViewerPipeline.kt` (new) — orchestrates control + transport + per-stream decoders.

- [ ] Step 1: tests for openStream lifecycle (mock TCP)
- [ ] Step 2: implement
- [ ] Step 3: commit `feat(viewer/control): MultiStreamControlClient with per-stream lifecycle flows`

### Task 5.4: Window picker UI

**Files:**
- Create: `viewer/.../app/ui/WindowPickerScreen.kt` — Compose UI showing one server's window list. Multi-select. Connect button opens `PanelSwitcherActivity`.
- Modify: `viewer/.../app/MainActivity.kt` — drop server-multi-select; route to `WindowPickerScreen` directly after server discovery.
- Delete: `viewer/.../app/ui/ServerPickerScreen.kt` (or keep for "no servers found" empty state if it has shared logic).
- Delete: `viewer/.../app/ServerSelectionActivity.kt` (if present).

- [ ] Step 1: ManualUI mockup screenshot for review
- [ ] Step 2: Write Compose component
- [ ] Step 3: Wire NSD + SERVER_HELLO flow
- [ ] Step 4: Test on adb
- [ ] Step 5: Commit `feat(viewer/ui): WindowPickerScreen — single-server window multi-select`

### Task 5.5: PanelSwitcherActivity (replaces DemoActivity)

**Files:**
- Create: `viewer/.../app/ui/PanelSwitcherActivity.kt` — full-screen panel display with tab bar across the top showing window titles for each open stream. Tap tab = switch active panel + send `FOCUS_WINDOW`.
- Delete: `viewer/.../demo/DemoActivity.kt` and `viewer/.../demo/DirectSurfaceFrameSink.kt` (move the latter to `app/ui/` if still useful).
- Reuse `MediaCodecDecoder`, `FrameSink`, `XrPanelSink`/`DirectSurfaceFrameSink`.

- [ ] Step 1: Compose layout
- [ ] Step 2: Wire MultiStreamViewerPipeline
- [ ] Step 3: Tab bar with focus dispatch
- [ ] Step 4: Pause/Resume on visibility change (panel off-screen → PAUSE_STREAM; visible → RESUME_STREAM)
- [ ] Step 5: Test on adb
- [ ] Step 6: Commit `feat(viewer/ui): PanelSwitcherActivity — one-panel-at-a-time switcher`

### Task 5.6: KeyEvent routing — streamId from active panel

**Files:**
- Modify: `viewer/.../app/ui/PanelSwitcherActivity.kt` (and `dispatchKeyEvent` plumbing) — `KeyEvent` carries `activePanel.streamId` rather than `streamStates[0]`.

- [ ] Step 1: pass active streamId
- [ ] Step 2: regression-test by typing into two notepads
- [ ] Step 3: commit `feat(viewer/input): KEY_EVENT carries focused panel's streamId`

### Task 5.7: Build + install + smoke test

- [ ] **Step 1: Build portable APK**

```bash
cd viewer/WindowStreamViewer
./gradlew :app:assemblePortableDebug
adb install -r app/build/outputs/apk/portable/debug/app-portable-debug.apk
```

- [ ] **Step 2: Launch coordinator on the PC**

```bash
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve
```

- [ ] **Step 3: Open viewer on Quest 3 / phone, walk through the picker, open 2 windows.**

- [ ] **Step 4: Commit any fixes**

---

## Phase 6 — Manual acceptance

Run the spec's manual acceptance checklist from §Testing:

- [ ] Open 3 streams simultaneously; close one; other two unaffected.
- [ ] Pause one stream; verify it freezes on last frame, others stay live.
- [ ] Focus stream A then B; type into a known editor in B; characters land in B's window.
- [ ] Kill `windowstream worker` for one stream via Task Manager; viewer sees `STREAM_STOPPED`; sibling streams unaffected.
- [ ] Open a window known to crash WGC (Task Manager — odd height); single stream dies; sibling streams unaffected; can close + reopen unrelated streams.
- [ ] Existing v1 acceptance items still pass (Notepad streams sharply, VS streams readably, panel reposition works on GXR via DemoActivity-equivalent path, disconnect/reconnect works).

If any check fails, capture as a follow-up bug and fix before declaring v2 done.

---

## Self-Review

**Spec coverage** — every spec section has at least one task:
- §Architecture (boxes + IPC) → Tasks 2.1–2.4, 3.1–3.7
- §Components (every named component) → Task 1.1 (WindowDescriptor), 1.4–1.5 (messages), 2.1–2.3 (worker), 3.1–3.6 (every coordinator component)
- §Data Flow (connect, open, pause, focus, close) → Tasks 4.2–4.7
- §Protocol (every new message + reason + error code) → Tasks 1.2–1.5
- §Error handling (worker dies, hangs, NVENC cap) → Tasks 4.4, 4.5, plus WorkerSupervisor's StreamHung detection (note: hang detection is captured in spec but not yet a separate task — handle in Task 3.2 by adding a `WorkerHangDetector` test case OR add an explicit Task 3.2.5; recommend adding a watchdog test in Task 4 phase as Task 4.4.5).
- §Testing (every named integration test) → Phase 4 tasks
- §Migration Notes (every retired/rewritten file) → Tasks 1.6, 3.7, 5.4, 5.5

**Placeholder scan** — Tasks 4.2–4.7 are described at section level rather than per-step. The implementing engineer has enough material from the harness in 4.1 plus the spec's described assertions to write each [Fact]. This is intentional: writing six full TDD cycles inline would balloon the doc; the harness in 4.1 carries the load.

Task 3.6 (CoordinatorControlServer) similarly describes the test cases as a list rather than emitting full inline test code — the implementing engineer drives the implementation via TDD using the existing `IControlChannel` mock pattern.

Both decisions trade some literal detail for readability. If the implementing engineer wants step-by-step code for these, they should expand them in-flight and commit the expansion.

**Type consistency** — `WindowDescriptor` (.NET) ↔ `WindowDescriptor` (Kotlin). `windowId` is `ulong` in .NET, `ULong` in Kotlin (JSON serializes as number). `streamId` is `int`. `StreamStoppedReason` matches the wire-name table on both sides. `WorkerCommandTag` byte values are stable and tested. ✓

**Outstanding decisions deferred to implementation:**
- LoadShedder concrete trigger thresholds (spec says implementation detail).
- PanelSwitcher tab-bar visual styling (UX-level, captured as judgment call).
- Worker hang detection threshold (suggested 5s; tunable).

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-04-27-multi-window-v2.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
