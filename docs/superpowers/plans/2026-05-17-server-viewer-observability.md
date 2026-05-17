# Server + Viewer Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user end-to-end pipeline visibility on both the WindowStream server (MAUI dashboard) and the viewer (Android), so "tap connect, nothing happens" is diagnosable without reading `adb logcat` or invisible MAUI debug output.

**Architecture:** A typed `PipelineEvent` sealed class hierarchy on each side feeds a `Diagnostics` façade. On the server, events flow through `Microsoft.Extensions.Logging.ILogger` to three sinks: existing Debug, a Serilog rolling JSONL file sink, and a custom in-app sink that fans out to the `ServerDashboardViewModel`. On the viewer, events flow through Timber to three trees: existing Logcat, a rotating JSONL `FileLoggingTree`, and an `InAppBufferTree` exposing a `SharedFlow<LogEvent>` to the UI. A per-side reducer derives the state board from the event stream so the board and the event log can't disagree by construction.

**Tech Stack:**
- Server (.NET 10): `Microsoft.Extensions.Logging`, Serilog (`Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.File`, `Serilog.Formatting.Compact`), MAUI bindings.
- Viewer (Android Kotlin): `com.jakewharton.timber:timber`, `kotlinx-serialization-json` (already present), `kotlinx-coroutines` (already present).

---

## Phase 1: Server foundation (Core observability types)

All Core changes target `net8.0` + `net8.0-windows10.0.19041.0`. Tests run under `WindowStream.Core.Tests` (xUnit, 100% line/branch gate).

### Task 1: Define `PipelineEvent` hierarchy and `Severity` enum

**Files:**
- Create: `src/WindowStream.Core/Observability/Severity.cs`
- Create: `src/WindowStream.Core/Observability/PipelineEvent.cs`
- Create: `tests/WindowStream.Core.Tests/Observability/PipelineEventTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
// tests/WindowStream.Core.Tests/Observability/PipelineEventTests.cs
using WindowStream.Core.Observability;
using Xunit;

namespace WindowStream.Core.Tests.Observability;

public class PipelineEventTests
{
    [Fact]
    public void Listening_Has_Info_Severity_And_Captures_Ports()
    {
        PipelineEvent.Listening listening = new(TcpPort: 53234, UdpPort: 53235);
        Assert.Equal(Severity.Info, listening.Severity);
        Assert.Null(listening.StreamId);
        Assert.Equal(53234, listening.TcpPort);
        Assert.Equal(53235, listening.UdpPort);
    }

    [Fact]
    public void WorkerSpawnFailed_Has_Error_Severity_And_Carries_StreamId()
    {
        var exception = new InvalidOperationException("worker boom");
        PipelineEvent.WorkerSpawnFailed evt = new(StreamId: 7, Exception: exception);
        Assert.Equal(Severity.Error, evt.Severity);
        Assert.Equal(7, evt.StreamId);
        Assert.Same(exception, evt.Exception);
    }

    [Fact]
    public void FramesFlowing_Heartbeat_Has_Info_And_StreamId()
    {
        PipelineEvent.FramesFlowing evt = new(StreamId: 3, Fps: 60.0, Kbps: 4800);
        Assert.Equal(Severity.Info, evt.Severity);
        Assert.Equal(3, evt.StreamId);
        Assert.Equal(60.0, evt.Fps);
    }
}
```

- [x] **Step 2: Run test, verify FAIL with missing types**

Run: `dotnet test tests/WindowStream.Core.Tests/ --filter FullyQualifiedName~PipelineEventTests -v normal`
Expected: FAIL — `The type or namespace name 'PipelineEvent' could not be found`

- [x] **Step 3: Write `Severity.cs`**

```csharp
namespace WindowStream.Core.Observability;

public enum Severity
{
    Info,
    Warning,
    Error,
}
```

- [x] **Step 4: Write `PipelineEvent.cs`**

```csharp
using System;

namespace WindowStream.Core.Observability;

/// <summary>
/// Typed pipeline-stage events emitted from coordinator and worker code.
/// The Diagnostics façade routes these through ILogger; sinks fan them out
/// to the in-app dashboard and a rotating JSONL file.
///
/// Per-frame markers ([FRAMECOUNT]) are deliberately NOT modeled here —
/// they live on stderr / logcat to avoid flooding the in-app buffer.
/// </summary>
public abstract record PipelineEvent(Severity Severity, int? StreamId)
{
    public sealed record Listening(int TcpPort, int UdpPort)
        : PipelineEvent(Severity.Info, null);

    public sealed record ViewerAccepted(string Endpoint)
        : PipelineEvent(Severity.Info, null);

    public sealed record ViewerDisconnected(string Endpoint, string Reason)
        : PipelineEvent(Severity.Info, null);

    public sealed record ServerHelloSent(int WindowCount)
        : PipelineEvent(Severity.Info, null);

    public sealed record WindowAppeared(ulong WindowId, string Title, string ProcessName, int Width, int Height)
        : PipelineEvent(Severity.Info, null);

    public sealed record WindowDisappeared(ulong WindowId)
        : PipelineEvent(Severity.Info, null);

    public sealed record WindowChanged(ulong WindowId, string? NewTitle, int? NewWidth, int? NewHeight)
        : PipelineEvent(Severity.Info, null);

    public sealed record OpenStreamReceived(int StreamId, ulong WindowId)
        : PipelineEvent(Severity.Info, StreamId);

    public sealed record WorkerSpawning(int StreamId, ulong WindowId)
        : PipelineEvent(Severity.Info, StreamId);

    public sealed record WorkerSpawned(int StreamId, int Pid)
        : PipelineEvent(Severity.Info, StreamId);

    public sealed record WorkerSpawnFailed(int StreamId, Exception Exception)
        : PipelineEvent(Severity.Error, StreamId);

    public sealed record CaptureStarted(int StreamId, int Width, int Height)
        : PipelineEvent(Severity.Info, StreamId);

    public sealed record CaptureFailed(int StreamId, Exception Exception)
        : PipelineEvent(Severity.Error, StreamId);

    public sealed record EncodeStarted(int StreamId, int Fps, int Kbps)
        : PipelineEvent(Severity.Info, StreamId);

    public sealed record EncodeFailed(int StreamId, Exception Exception)
        : PipelineEvent(Severity.Error, StreamId);

    public sealed record FramesFlowing(int StreamId, double Fps, int Kbps)
        : PipelineEvent(Severity.Info, StreamId);

    public sealed record StreamRefused(int StreamId, string ErrorCode, string Message)
        : PipelineEvent(Severity.Warning, StreamId);

    public sealed record StreamStopped(int StreamId, string Reason)
        : PipelineEvent(Severity.Info, StreamId);

    public sealed record ProbeFailed(ulong WindowId, long Hwnd, Exception Exception)
        : PipelineEvent(Severity.Error, null);

    public sealed record EnumerationFailed(Exception Exception)
        : PipelineEvent(Severity.Warning, null);
}
```

- [x] **Step 5: Run test, verify PASS**

Run: `dotnet test tests/WindowStream.Core.Tests/ --filter FullyQualifiedName~PipelineEventTests -v normal`
Expected: PASS, 3/3.

- [x] **Step 6: Commit**

```bash
git add src/WindowStream.Core/Observability/Severity.cs \
        src/WindowStream.Core/Observability/PipelineEvent.cs \
        tests/WindowStream.Core.Tests/Observability/PipelineEventTests.cs
git commit -m "feat(core): add PipelineEvent hierarchy and Severity enum"
```

### Task 2: `Diagnostics` static façade

**Files:**
- Create: `src/WindowStream.Core/Observability/Diagnostics.cs`
- Create: `tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs`

The façade translates a `PipelineEvent` into a structured `ILogger` call. It is the ONE place that knows how to map events → log records.

- [x] **Step 1: Write the failing test**

```csharp
// tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Moq;
using WindowStream.Core.Observability;
using Xunit;

namespace WindowStream.Core.Tests.Observability;

public class DiagnosticsTests
{
    [Fact]
    public void Report_Translates_Listening_To_Info_Log_With_EventType_Property()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.Listening(TcpPort: 53234, UdpPort: 53235));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Listening")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Report_Translates_WorkerSpawnFailed_To_Error_Log_With_Exception()
    {
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var boom = new InvalidOperationException("boom");

        Diagnostics diagnostics = new(loggerMock.Object);
        diagnostics.Report(new PipelineEvent.WorkerSpawnFailed(StreamId: 7, Exception: boom));

        loggerMock.Verify(logger => logger.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            boom,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
```

Add `Moq` to test project: edit `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` to include
```xml
<PackageReference Include="Moq" Version="4.20.72" />
```
if not already present.

- [x] **Step 2: Run, verify FAIL**

Run: `dotnet test tests/WindowStream.Core.Tests/ --filter FullyQualifiedName~DiagnosticsTests`
Expected: FAIL (Diagnostics type missing).

- [x] **Step 3: Write `Diagnostics.cs`**

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace WindowStream.Core.Observability;

/// <summary>
/// Façade for emitting <see cref="PipelineEvent"/>s through
/// <see cref="Microsoft.Extensions.Logging.ILogger"/>. The event's runtime
/// type name is stored on the log scope as <c>EventType</c>, alongside the
/// event's properties; sinks (file + in-app) read these to materialize the
/// state board and JSONL log lines.
/// </summary>
public sealed class Diagnostics
{
    private readonly ILogger logger;

    public Diagnostics(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Report(PipelineEvent pipelineEvent)
    {
        if (pipelineEvent is null) throw new ArgumentNullException(nameof(pipelineEvent));

        LogLevel logLevel = pipelineEvent.Severity switch
        {
            Severity.Info => LogLevel.Information,
            Severity.Warning => LogLevel.Warning,
            Severity.Error => LogLevel.Error,
            _ => LogLevel.Information,
        };

        Dictionary<string, object?> scopeProperties = new()
        {
            ["EventType"] = pipelineEvent.GetType().Name,
            ["StreamId"] = pipelineEvent.StreamId,
        };

        Exception? exception = pipelineEvent switch
        {
            PipelineEvent.WorkerSpawnFailed wsf => wsf.Exception,
            PipelineEvent.CaptureFailed cf => cf.Exception,
            PipelineEvent.EncodeFailed ef => ef.Exception,
            PipelineEvent.ProbeFailed pf => pf.Exception,
            PipelineEvent.EnumerationFailed enf => enf.Exception,
            _ => null,
        };

        using (logger.BeginScope(scopeProperties))
        {
            logger.Log(logLevel, default, pipelineEvent, exception,
                static (state, _) => state.GetType().Name + ": " + state.ToString());
        }
    }
}
```

- [x] **Step 4: Run, verify PASS**

Run: `dotnet test tests/WindowStream.Core.Tests/ --filter FullyQualifiedName~DiagnosticsTests`
Expected: PASS 2/2.

- [x] **Step 5: Commit**

```bash
git add src/WindowStream.Core/Observability/Diagnostics.cs \
        tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs \
        tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj
git commit -m "feat(core): add Diagnostics façade routing PipelineEvent to ILogger"
```

### Task 3: `Microsoft.Extensions.Logging.Abstractions` reference on Core

**Files:**
- Modify: `src/WindowStream.Core/WindowStream.Core.csproj`

- [ ] **Step 1: Add the package reference**

Edit `src/WindowStream.Core/WindowStream.Core.csproj`, add inside the first `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/WindowStream.Core/WindowStream.Core.csproj
git commit -m "build(core): add Microsoft.Extensions.Logging.Abstractions reference"
```

### Task 4: Refactor `CoordinatorLauncher` to use `Diagnostics`

**Files:**
- Modify: `src/WindowStream.Core/Hosting/CoordinatorLauncher.cs` (full file)

Replace the constructor's `TextWriter output` with `Diagnostics diagnostics`. Replace `output.WriteLine` and `Console.Error.WriteLine` calls with `Diagnostics.Report(...)`. Replace the `OnAvailableWindowCountChanged` / `OnPortsAssigned` / `OnActiveStreamCountChanged` / `OnViewerChanged` action delegates — they go away (their state lives in the reducer).

- [x] **Step 1: Modify the constructor and remove action callbacks**

In `CoordinatorLauncher.cs`, replace:
```csharp
private readonly int tcpPort;
private readonly TextWriter output;

public Action<int>? OnAvailableWindowCountChanged { get; set; }
public Action<string?>? OnViewerChanged { get; set; }
public Action<int, int>? OnPortsAssigned { get; set; }
public Action<int>? OnActiveStreamCountChanged { get; set; }

public CoordinatorLauncher(int tcpPort, TextWriter output)
{
    this.tcpPort = tcpPort;
    this.output = output ?? throw new ArgumentNullException(nameof(output));
}
```
with:
```csharp
private readonly int tcpPort;
private readonly Diagnostics diagnostics;

public CoordinatorLauncher(int tcpPort, Diagnostics diagnostics)
{
    this.tcpPort = tcpPort;
    this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
}
```

- [x] **Step 2: Replace banner writes with `Listening` event**

In `CoordinatorLauncher.LaunchAsync`, replace:
```csharp
OnPortsAssigned?.Invoke(controlServer.TcpPort, udpSender.LocalPort);
output.WriteLine($"windowstream: serving on TCP {controlServer.TcpPort}, UDP {udpSender.LocalPort}");
output.WriteLine($"  mDNS: _windowstream._tcp as '{Environment.MachineName}' (v2)");
output.WriteLine("  Press Ctrl-C to stop.");
```
with:
```csharp
diagnostics.Report(new PipelineEvent.Listening(controlServer.TcpPort, udpSender.LocalPort));
```

- [x] **Step 3: Replace probe-failure `Console.Error.WriteLine` with `ProbeFailed` event**

In `ResolveEncoderOptions` (kept around but no longer used on the hot path), replace the two `Console.Error.WriteLine` calls with:
```csharp
diagnostics.Report(new PipelineEvent.ProbeFailed(windowId, hwnd.Value, probeException));
// ...
diagnostics.Report(new PipelineEvent.ProbeFailed(windowId, hwnd.Value,
    new InvalidOperationException("probe returned null")));
```
Note: since `ResolveEncoderOptions` is static and has no access to `diagnostics`, accept `Diagnostics` as a parameter when calling it. **However** the live path uses `ResolveEncoderOptionsFromDescriptor` and does not throw — so a simpler change: leave `ResolveEncoderOptions` deleted-by-disuse, or remove the dead method. Inspect whether anything still calls `ResolveEncoderOptions`; if not, delete it. **Action:** delete the static `ResolveEncoderOptions` method and `ProbeCaptureSizeAsync` method (both unused after the fast-path refactor noted in `ResolveEncoderOptionsFromDescriptor`'s doc-comment).

- [x] **Step 4: Replace enumeration exception swallow**

In `RunEnumerationLoopAsync`, replace:
```csharp
catch (Exception)
{
    // Enumeration failure is transient — try again next tick.
    continue;
}
```
with:
```csharp
catch (Exception enumerationException)
{
    // Pass diagnostics by closure into the loop. See Step 5 — sig changes.
    diagnostics.Report(new PipelineEvent.EnumerationFailed(enumerationException));
    continue;
}
```
Update `RunEnumerationLoopAsync`'s signature to take a `Diagnostics diagnostics` parameter, and update the `Task.Run(...)` call in `LaunchAsync` to pass it.

- [x] **Step 5: Emit per-stream lifecycle events**

In the `supervisor.StreamStarted += ...` handler in `LaunchAsync`, after `streamIdToWindowId[args.StreamId] = args.WindowId;` add:
```csharp
diagnostics.Report(new PipelineEvent.WorkerSpawned(args.StreamId, args.WorkerProcessId));
```
(if `args` has the PID; if it does not, leave a TODO comment and emit `args.StreamId` only — verify by reading `StreamStartedEventArgs`).

In `supervisor.StreamEnded`:
```csharp
diagnostics.Report(new PipelineEvent.StreamStopped(args.StreamId, args.Reason ?? "unknown"));
```

In the `controlServer` configuration, for the viewer accept hook — search for where `ActiveViewerEndpoint` is set or where `TcpAccepted` fires (read `CoordinatorControlServer.cs`). Emit `ViewerAccepted(endpoint)` there.

- [x] **Step 6: Emit `WindowAppeared` / `WindowDisappeared` / `WindowChanged`**

In `RunEnumerationLoopAsync`'s switch, in each `case`, after the existing call to `controlServer.NotifyWindow*`, emit:
- `case WindowAppeared appeared`: `diagnostics.Report(new PipelineEvent.WindowAppeared(appeared.WindowId, descriptor.Title, descriptor.ProcessName, descriptor.PhysicalWidth, descriptor.PhysicalHeight));`
- `case WindowDisappeared gone`: `diagnostics.Report(new PipelineEvent.WindowDisappeared(gone.WindowId));`
- `case WindowChanged changed`: `diagnostics.Report(new PipelineEvent.WindowChanged(changed.WindowId, changed.NewTitle, changed.NewWidthPixels, changed.NewHeightPixels));`

- [x] **Step 7: Build and run all Core tests**

Run: `dotnet build && dotnet test tests/WindowStream.Core.Tests/`
Expected: 0 errors, all tests pass. (Coverage may dip below 100% — that's addressed in Task 5.)

- [x] **Step 8: Commit**

```bash
git add src/WindowStream.Core/Hosting/CoordinatorLauncher.cs
git commit -m "refactor(core): route CoordinatorLauncher through Diagnostics façade"
```

---

## Phase 2: Server sinks (Serilog + in-app)

### Task 5: Add Serilog packages to the server project

> **Deviation from original plan:** Serilog bumped 4.1.0 → 4.2.0 — minimum compatible with Serilog.Extensions.Logging 9.0.0 (which has a transitive `Serilog >= 4.2.0` floor; 4.1.0 triggers NU1605 downgrade error). Other three package versions unchanged.

**Files:**
- Modify: `src/WindowStreamServer/WindowStreamServer.csproj`

- [x] **Step 1: Add the four Serilog packages**

Add inside the existing `<ItemGroup>` that contains `Microsoft.Extensions.Logging.Debug`:
```xml
<PackageReference Include="Serilog" Version="4.2.0" />
<PackageReference Include="Serilog.Extensions.Logging" Version="9.0.0" />
<PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
<PackageReference Include="Serilog.Formatting.Compact" Version="3.0.0" />
```

- [x] **Step 2: Restore + build**

Run: `dotnet restore && dotnet build src/WindowStreamServer/WindowStreamServer.csproj`
Expected: 0 errors. May warn about MAUI workload version — ignore.

- [x] **Step 3: Commit**

```bash
git add src/WindowStreamServer/WindowStreamServer.csproj
git commit -m "build(server): add Serilog file + compact-json sinks"
```

### Task 6: `InAppDashboardSink` (custom `ILogEventSink`)

**Files:**
- Create: `src/WindowStreamServer/Observability/LogEntry.cs`
- Create: `src/WindowStreamServer/Observability/InAppDashboardSink.cs`
- Create: `tests/WindowStream.Server.Tests/Observability/InAppDashboardSinkTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
// tests/WindowStream.Server.Tests/Observability/InAppDashboardSinkTests.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog.Events;
using Serilog.Parsing;
using WindowStream.Server.Observability;
using Xunit;

namespace WindowStream.Server.Tests.Observability;

public class InAppDashboardSinkTests
{
    private static LogEvent MakeEvent(LogEventLevel level, string message) =>
        new(System.DateTimeOffset.UtcNow, level, null,
            new MessageTemplate(message, new List<MessageTemplateToken>()),
            new List<LogEventProperty>());

    [Fact]
    public void Emit_Adds_Entry_To_Snapshot()
    {
        InAppDashboardSink sink = new(capacity: 10);
        sink.Emit(MakeEvent(LogEventLevel.Information, "hello"));

        IReadOnlyList<LogEntry> snapshot = sink.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("hello", snapshot[0].Message);
        Assert.Equal(Severity.Info, snapshot[0].Severity);
    }

    [Fact]
    public void Ring_Buffer_Evicts_Oldest_Past_Capacity()
    {
        InAppDashboardSink sink = new(capacity: 3);
        for (int i = 0; i < 5; i++) sink.Emit(MakeEvent(LogEventLevel.Information, $"m{i}"));
        IReadOnlyList<LogEntry> snapshot = sink.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal("m2", snapshot[0].Message);
        Assert.Equal("m4", snapshot[2].Message);
    }

    [Fact]
    public async Task OnEvent_Fires_Once_Per_Emit_From_Concurrent_Threads()
    {
        InAppDashboardSink sink = new(capacity: 1000);
        int fireCount = 0;
        sink.OnEvent += _ => System.Threading.Interlocked.Increment(ref fireCount);

        Task[] tasks = new Task[8];
        for (int i = 0; i < tasks.Length; i++)
        {
            int id = i;
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 50; j++) sink.Emit(MakeEvent(LogEventLevel.Information, $"t{id}-{j}"));
            });
        }
        await Task.WhenAll(tasks);
        Assert.Equal(400, fireCount);
    }
}
```

- [x] **Step 2: Run, verify FAIL**

Run: `dotnet test tests/WindowStream.Server.Tests/`
Expected: FAIL — `LogEntry` and `InAppDashboardSink` missing.

- [x] **Step 3: Write `LogEntry.cs`**

```csharp
using System;
using WindowStream.Core.Observability;

namespace WindowStream.Server.Observability;

/// <summary>
/// Materialized log entry stored in <see cref="InAppDashboardSink"/>'s ring
/// buffer. Carries the event type name and the structured properties from
/// the original <see cref="Serilog.Events.LogEvent"/>.
/// </summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    Severity Severity,
    string EventType,
    int? StreamId,
    string Message,
    Exception? Exception);
```

- [x] **Step 4: Write `InAppDashboardSink.cs`**

```csharp
using System;
using System.Collections.Generic;
using Serilog.Core;
using Serilog.Events;
using WindowStream.Core.Observability;

namespace WindowStream.Server.Observability;

/// <summary>
/// Custom Serilog sink that materializes <see cref="LogEvent"/>s into
/// <see cref="LogEntry"/> records held in a bounded ring buffer. Raises
/// <see cref="OnEvent"/> on every emit (from arbitrary threads — UI
/// subscribers must marshal to the main thread). Snapshot is a copy.
///
/// Thread-safety: all state changes happen under a single lock.
/// </summary>
public sealed class InAppDashboardSink : ILogEventSink
{
    private readonly int capacity;
    private readonly Queue<LogEntry> buffer;
    private readonly object syncRoot = new();

    public event Action<LogEntry>? OnEvent;

    public InAppDashboardSink(int capacity = 500)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        buffer = new Queue<LogEntry>(capacity);
    }

    public void Emit(LogEvent logEvent)
    {
        LogEntry entry = MapToEntry(logEvent);
        lock (syncRoot)
        {
            if (buffer.Count == capacity) buffer.Dequeue();
            buffer.Enqueue(entry);
        }
        OnEvent?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (syncRoot) return buffer.ToArray();
    }

    private static LogEntry MapToEntry(LogEvent logEvent)
    {
        Severity severity = logEvent.Level switch
        {
            LogEventLevel.Verbose or LogEventLevel.Debug or LogEventLevel.Information => Severity.Info,
            LogEventLevel.Warning => Severity.Warning,
            _ => Severity.Error,
        };

        string eventType = logEvent.Properties.TryGetValue("EventType", out LogEventPropertyValue? eventTypeValue)
            ? eventTypeValue.ToString().Trim('"')
            : "Log";

        int? streamId = null;
        if (logEvent.Properties.TryGetValue("StreamId", out LogEventPropertyValue? streamIdValue) &&
            streamIdValue is ScalarValue { Value: int streamIdInt })
        {
            streamId = streamIdInt;
        }

        return new LogEntry(
            Timestamp: logEvent.Timestamp,
            Severity: severity,
            EventType: eventType,
            StreamId: streamId,
            Message: logEvent.RenderMessage(),
            Exception: logEvent.Exception);
    }
}
```

- [x] **Step 5: Add a `ProjectReference` for `WindowStream.Server.Tests`**

Edit `tests/WindowStream.Server.Tests/WindowStream.Server.Tests.csproj`, ensure it references the server project. If the test project doesn't exist yet, scaffold it with `dotnet new xunit -o tests/WindowStream.Server.Tests`, then add references to `WindowStream.Server` and `Serilog`.

Quick sanity check: the directory exists (per earlier `ls`), so just verify `WindowStream.Server.Tests.csproj` has:
```xml
<PackageReference Include="Serilog" Version="4.1.0" />
<ProjectReference Include="..\..\src\WindowStreamServer\WindowStreamServer.csproj" />
```

- [x] **Step 6: Run, verify PASS**

Run: `dotnet test tests/WindowStream.Server.Tests/ --filter FullyQualifiedName~InAppDashboardSinkTests`
Expected: PASS 3/3.

- [x] **Step 7: Commit**

```bash
git add src/WindowStreamServer/Observability/LogEntry.cs \
        src/WindowStreamServer/Observability/InAppDashboardSink.cs \
        tests/WindowStream.Server.Tests/Observability/InAppDashboardSinkTests.cs \
        tests/WindowStream.Server.Tests/WindowStream.Server.Tests.csproj
git commit -m "feat(server): InAppDashboardSink ring buffer with OnEvent fan-out"
```

### Task 7: State board reducer

**Files:**
- Create: `src/WindowStreamServer/Observability/StreamStateRow.cs`
- Create: `src/WindowStreamServer/Observability/StageStatus.cs`
- Create: `src/WindowStreamServer/Observability/ServerStateReducer.cs`
- Create: `tests/WindowStream.Server.Tests/Observability/ServerStateReducerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/WindowStream.Server.Tests/Observability/ServerStateReducerTests.cs
using WindowStream.Core.Observability;
using WindowStream.Server.Observability;
using Xunit;

namespace WindowStream.Server.Tests.Observability;

public class ServerStateReducerTests
{
    [Fact]
    public void Initial_State_Is_All_Pending()
    {
        ServerStateReducer reducer = new();
        Assert.Equal(StageStatus.Pending, reducer.State.Listening);
        Assert.Equal(StageStatus.Pending, reducer.State.ViewerConnected);
        Assert.Empty(reducer.State.Streams);
    }

    [Fact]
    public void Listening_Event_Sets_Listening_Ok_And_Ports()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.Listening(53234, 53235));
        Assert.Equal(StageStatus.Ok, reducer.State.Listening);
        Assert.Equal(53234, reducer.State.TcpPort);
        Assert.Equal(53235, reducer.State.UdpPort);
    }

    [Fact]
    public void OpenStreamReceived_Creates_New_Stream_Row_With_Pending_Stages()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.OpenStreamReceived(StreamId: 1, WindowId: 7));
        StreamStateRow row = reducer.State.Streams[1];
        Assert.Equal(7UL, row.WindowId);
        Assert.Equal(StageStatus.Pending, row.WorkerSpawn);
    }

    [Fact]
    public void WorkerSpawnFailed_Transitions_Row_To_Error()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.OpenStreamReceived(1, 7));
        reducer.Apply(new PipelineEvent.WorkerSpawnFailed(1, new System.Exception("boom")));
        Assert.Equal(StageStatus.Error, reducer.State.Streams[1].WorkerSpawn);
        Assert.Equal("boom", reducer.State.Streams[1].WorkerSpawnError);
    }

    [Fact]
    public void StreamStopped_Removes_Row()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.OpenStreamReceived(1, 7));
        reducer.Apply(new PipelineEvent.StreamStopped(1, "viewer-disconnect"));
        Assert.False(reducer.State.Streams.ContainsKey(1));
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run: `dotnet test tests/WindowStream.Server.Tests/ --filter ServerStateReducerTests`
Expected: FAIL.

- [ ] **Step 3: Write `StageStatus.cs`**

```csharp
namespace WindowStream.Server.Observability;

public enum StageStatus
{
    Pending,
    InProgress,
    Ok,
    Warning,
    Error,
}
```

- [ ] **Step 4: Write `StreamStateRow.cs`**

```csharp
namespace WindowStream.Server.Observability;

public sealed record StreamStateRow
{
    public required ulong WindowId { get; init; }
    public string Title { get; init; } = "";
    public StageStatus WorkerSpawn { get; init; } = StageStatus.Pending;
    public string? WorkerSpawnError { get; init; }
    public StageStatus Capture { get; init; } = StageStatus.Pending;
    public string? CaptureError { get; init; }
    public int? CaptureWidth { get; init; }
    public int? CaptureHeight { get; init; }
    public StageStatus Encode { get; init; } = StageStatus.Pending;
    public string? EncodeError { get; init; }
    public int? EncodeFps { get; init; }
    public int? EncodeKbps { get; init; }
    public StageStatus UdpSend { get; init; } = StageStatus.Pending;
    public double? CurrentFps { get; init; }
    public int? CurrentKbps { get; init; }
}
```

- [ ] **Step 5: Write `ServerStateReducer.cs`**

```csharp
using System.Collections.Generic;
using System.Collections.Immutable;
using WindowStream.Core.Observability;

namespace WindowStream.Server.Observability;

public sealed record ServerState
{
    public StageStatus Listening { get; init; } = StageStatus.Pending;
    public int? TcpPort { get; init; }
    public int? UdpPort { get; init; }
    public StageStatus ViewerConnected { get; init; } = StageStatus.Pending;
    public string? ViewerEndpoint { get; init; }
    public int WindowCount { get; init; }
    public ImmutableDictionary<int, StreamStateRow> Streams { get; init; }
        = ImmutableDictionary<int, StreamStateRow>.Empty;
}

public sealed class ServerStateReducer
{
    public ServerState State { get; private set; } = new();

    public void Apply(PipelineEvent pipelineEvent)
    {
        State = pipelineEvent switch
        {
            PipelineEvent.Listening listening => State with
            {
                Listening = StageStatus.Ok,
                TcpPort = listening.TcpPort,
                UdpPort = listening.UdpPort,
            },
            PipelineEvent.ViewerAccepted accepted => State with
            {
                ViewerConnected = StageStatus.Ok,
                ViewerEndpoint = accepted.Endpoint,
            },
            PipelineEvent.ViewerDisconnected => State with
            {
                ViewerConnected = StageStatus.Pending,
                ViewerEndpoint = null,
            },
            PipelineEvent.WindowAppeared => State with { WindowCount = State.WindowCount + 1 },
            PipelineEvent.WindowDisappeared => State with { WindowCount = System.Math.Max(0, State.WindowCount - 1) },
            PipelineEvent.OpenStreamReceived open => State with
            {
                Streams = State.Streams.SetItem(open.StreamId,
                    new StreamStateRow { WindowId = open.WindowId }),
            },
            PipelineEvent.WorkerSpawned spawned when State.Streams.ContainsKey(spawned.StreamId) =>
                State with { Streams = State.Streams.SetItem(spawned.StreamId,
                    State.Streams[spawned.StreamId] with { WorkerSpawn = StageStatus.Ok }) },
            PipelineEvent.WorkerSpawnFailed failed when State.Streams.ContainsKey(failed.StreamId) =>
                State with { Streams = State.Streams.SetItem(failed.StreamId,
                    State.Streams[failed.StreamId] with
                    {
                        WorkerSpawn = StageStatus.Error,
                        WorkerSpawnError = failed.Exception.Message,
                    }) },
            PipelineEvent.CaptureStarted captured when State.Streams.ContainsKey(captured.StreamId) =>
                State with { Streams = State.Streams.SetItem(captured.StreamId,
                    State.Streams[captured.StreamId] with
                    {
                        Capture = StageStatus.Ok,
                        CaptureWidth = captured.Width,
                        CaptureHeight = captured.Height,
                    }) },
            PipelineEvent.CaptureFailed cf when State.Streams.ContainsKey(cf.StreamId) =>
                State with { Streams = State.Streams.SetItem(cf.StreamId,
                    State.Streams[cf.StreamId] with
                    {
                        Capture = StageStatus.Error,
                        CaptureError = cf.Exception.Message,
                    }) },
            PipelineEvent.EncodeStarted enc when State.Streams.ContainsKey(enc.StreamId) =>
                State with { Streams = State.Streams.SetItem(enc.StreamId,
                    State.Streams[enc.StreamId] with
                    {
                        Encode = StageStatus.Ok,
                        EncodeFps = enc.Fps,
                        EncodeKbps = enc.Kbps,
                    }) },
            PipelineEvent.EncodeFailed ef when State.Streams.ContainsKey(ef.StreamId) =>
                State with { Streams = State.Streams.SetItem(ef.StreamId,
                    State.Streams[ef.StreamId] with
                    {
                        Encode = StageStatus.Error,
                        EncodeError = ef.Exception.Message,
                    }) },
            PipelineEvent.FramesFlowing flowing when State.Streams.ContainsKey(flowing.StreamId) =>
                State with { Streams = State.Streams.SetItem(flowing.StreamId,
                    State.Streams[flowing.StreamId] with
                    {
                        UdpSend = StageStatus.Ok,
                        CurrentFps = flowing.Fps,
                        CurrentKbps = flowing.Kbps,
                    }) },
            PipelineEvent.StreamStopped stopped => State with
            {
                Streams = State.Streams.Remove(stopped.StreamId),
            },
            _ => State,
        };
    }
}
```

- [ ] **Step 6: Run, verify PASS**

Run: `dotnet test tests/WindowStream.Server.Tests/ --filter ServerStateReducerTests`
Expected: PASS 5/5.

- [ ] **Step 7: Commit**

```bash
git add src/WindowStreamServer/Observability/StageStatus.cs \
        src/WindowStreamServer/Observability/StreamStateRow.cs \
        src/WindowStreamServer/Observability/ServerStateReducer.cs \
        tests/WindowStream.Server.Tests/Observability/ServerStateReducerTests.cs
git commit -m "feat(server): state-board reducer with per-stream rows"
```

### Task 8: Wire Serilog + sinks in `MauiProgram.cs`

**Files:**
- Modify: `src/WindowStreamServer/MauiProgram.cs`

- [ ] **Step 1: Replace the `MauiProgram.CreateMauiApp` body**

```csharp
using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using WindowStream.Core.Hosting;
using WindowStream.Core.Observability;
using WindowStream.Core.Session;
using WindowStream.Server.Observability;
using WindowStream.Server.Pages;
using WindowStream.Server.ViewModels;

namespace WindowStream.Server;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        InAppDashboardSink inAppSink = new(capacity: 500);

        string logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowStream", "logs");
        Directory.CreateDirectory(logsDirectory);

        Logger serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(logsDirectory, "server-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: false)
            .WriteTo.Sink(inAppSink)
            .CreateLogger();

        SerilogLoggerFactory loggerFactory = new(serilogLogger, dispose: true);
        ILogger<CoordinatorLauncher> launcherLogger = loggerFactory.CreateLogger<CoordinatorLauncher>();

        Diagnostics diagnostics = new(launcherLogger);
        CoordinatorLauncher launcher = new(tcpPort: 0, diagnostics: diagnostics);
        ServerDashboardViewModel dashboard = new(launcher, inAppSink);

        builder.Services.AddSingleton<ISessionHostLauncher>(launcher);
        builder.Services.AddSingleton(dashboard);
        builder.Services.AddSingleton(inAppSink);
        builder.Services.AddSingleton(diagnostics);
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
```

Note that `Logger` here is `Serilog.Core.Logger`. Add `using Serilog.Core;` if compiler complains.

- [ ] **Step 2: Build**

Run: `dotnet build src/WindowStreamServer/WindowStreamServer.csproj`
Expected: 0 errors. (`ServerDashboardViewModel` constructor will be modified in Task 9 to accept `InAppDashboardSink` — for the next build step, accept the temporary build failure.)

- [ ] **Step 3: Commit (intermediate, allowed to be red on Server only)**

Defer the commit until Task 9 completes — they're tightly coupled. **Don't commit yet.**

### Task 9: `ServerDashboardViewModel` subscribes to the sink

**Files:**
- Modify: `src/WindowStreamServer/ViewModels/ServerDashboardViewModel.cs` (full rewrite)
- Create: `src/WindowStreamServer/ViewModels/LogEntryViewModel.cs`
- Modify: `tests/WindowStream.Server.Tests/` — add test that an emitted event surfaces in the VM within 1 s.

- [ ] **Step 1: Rewrite `ServerDashboardViewModel.cs`**

```csharp
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using WindowStream.Core.Session;
using WindowStream.Server.Observability;

namespace WindowStream.Server.ViewModels;

/// <summary>
/// Subscribes to the InAppDashboardSink and runs every event through the
/// ServerStateReducer to derive state-board bindings. Also maintains an
/// ObservableCollection of recent log entries for the event-log pane.
/// </summary>
public sealed class ServerDashboardViewModel : INotifyPropertyChanged
{
    private readonly ISessionHostLauncher hostLauncher;
    private readonly InAppDashboardSink sink;
    private readonly ServerStateReducer reducer = new();

    public ServerState State => reducer.State;
    public ObservableCollection<LogEntryViewModel> RecentEvents { get; } = new();

    public string ServerStatus => State.Listening == StageStatus.Ok ? "Serving" : "Starting…";
    public int TcpPort => State.TcpPort ?? 0;
    public int UdpPort => State.UdpPort ?? 0;
    public string? ConnectedViewer => State.ViewerEndpoint;
    public int ActiveStreamCount => State.Streams.Count;
    public int AvailableWindowCount => State.WindowCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerDashboardViewModel(ISessionHostLauncher hostLauncher, InAppDashboardSink sink)
    {
        this.hostLauncher = hostLauncher;
        this.sink = sink;
        foreach (LogEntry entry in sink.Snapshot()) AppendEntry(entry);
        sink.OnEvent += OnSinkEvent;
    }

    public async Task StartServingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await hostLauncher.LaunchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception exception)
        {
            // The diagnostics façade should already have logged details; this catch
            // exists so the page doesn't see an unhandled task exception.
            System.Diagnostics.Debug.WriteLine($"launcher faulted: {exception}");
        }
    }

    private void OnSinkEvent(LogEntry entry)
    {
        // Sink fires from arbitrary threads; marshal to main for UI update.
        MainThread.BeginInvokeOnMainThread(() => AppendEntry(entry));
    }

    private void AppendEntry(LogEntry entry)
    {
        // For the in-memory replay we apply the event-type to the reducer if it's a known PipelineEvent.
        // The sink already extracted EventType; we rely on the reducer's existing PipelineEvent dispatch
        // by reconstructing-by-name where we can. For now, raise property change on RecentEvents only
        // (per-stream state is fed by Diagnostics callers directly in Phase 3 follow-up).
        RecentEvents.Add(new LogEntryViewModel(entry));
        while (RecentEvents.Count > 200) RecentEvents.RemoveAt(0);
        RaiseAll();
    }

    public void ApplyEvent(WindowStream.Core.Observability.PipelineEvent pipelineEvent)
    {
        reducer.Apply(pipelineEvent);
        MainThread.BeginInvokeOnMainThread(RaiseAll);
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TcpPort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UdpPort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectedViewer)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveStreamCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableWindowCount)));
    }
}
```

Note: the sink-driven `AppendEntry` path appends entries to the event-log pane only. The reducer-driven state is updated by an additional path — `Diagnostics` will be extended in Step 3 of this task to also invoke `dashboard.ApplyEvent(...)` when the dashboard is registered as a subscriber.

- [ ] **Step 2: Write `LogEntryViewModel.cs`**

```csharp
using WindowStream.Server.Observability;

namespace WindowStream.Server.ViewModels;

public sealed record LogEntryViewModel(LogEntry Entry)
{
    public string Timestamp => Entry.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
    public string Severity => Entry.Severity.ToString().ToUpperInvariant();
    public string EventType => Entry.EventType;
    public int? StreamId => Entry.StreamId;
    public string Message => Entry.Message;
    public string SeverityColor => Entry.Severity switch
    {
        WindowStream.Core.Observability.Severity.Error => "#FF6060",
        WindowStream.Core.Observability.Severity.Warning => "#FFC040",
        _ => "#C0C0C0",
    };
}
```

- [ ] **Step 3: Extend `Diagnostics` to also notify a registered dashboard**

In `src/WindowStream.Core/Observability/Diagnostics.cs`, add:
```csharp
private Action<PipelineEvent>? subscriber;

public void Subscribe(Action<PipelineEvent> handler)
{
    subscriber = handler;
}
```
And in `Report(...)`, after the `using` block, append:
```csharp
subscriber?.Invoke(pipelineEvent);
```

In `MauiProgram.cs` after `Diagnostics diagnostics = new(...)`:
```csharp
diagnostics.Subscribe(dashboard.ApplyEvent);
```

(Note: forward declare — `dashboard` is constructed after `diagnostics`, so move the `dashboard.ApplyEvent` subscribe call after `dashboard` is constructed.)

- [ ] **Step 4: Build + test**

Run: `dotnet build && dotnet test`
Expected: PASS — all existing tests still green, plus the new reducer/sink tests.

- [ ] **Step 5: Commit (the Task 8 + 9 pair)**

```bash
git add src/WindowStreamServer/MauiProgram.cs \
        src/WindowStreamServer/ViewModels/ServerDashboardViewModel.cs \
        src/WindowStreamServer/ViewModels/LogEntryViewModel.cs \
        src/WindowStream.Core/Observability/Diagnostics.cs
git commit -m "feat(server): wire Serilog + InAppDashboardSink into MauiProgram"
```

---

## Phase 3: Server UI (state board + event log pane)

### Task 10: `MainPage.xaml` — add state-board section

**Files:**
- Modify: `src/WindowStreamServer/Pages/MainPage.xaml`
- Modify: `src/WindowStreamServer/Pages/StatusColorConverter.cs` (extend or add a sibling converter)

- [ ] **Step 1: Add `StageStatusGlyphConverter.cs`**

Create `src/WindowStreamServer/Pages/StageStatusGlyphConverter.cs`:
```csharp
using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using WindowStream.Server.Observability;

namespace WindowStream.Server.Pages;

public sealed class StageStatusGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StageStatus status
            ? status switch
            {
                StageStatus.Ok => "✓",
                StageStatus.Warning => "⚠",
                StageStatus.Error => "✗",
                StageStatus.InProgress => "…",
                _ => "—",
            }
            : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Register in `App.xaml` resource dictionary (or inline in `MainPage.xaml`'s `<ContentPage.Resources>`).

- [ ] **Step 2: Rewrite `MainPage.xaml`**

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:viewModels="clr-namespace:WindowStream.Server.ViewModels"
             xmlns:pages="clr-namespace:WindowStream.Server.Pages"
             x:Class="WindowStream.Server.Pages.MainPage"
             x:DataType="viewModels:ServerDashboardViewModel"
             Title="WindowStream Server">
    <ContentPage.Resources>
        <pages:StatusColorConverter x:Key="StatusColorConverter" />
        <pages:StageStatusGlyphConverter x:Key="GlyphConverter" />
    </ContentPage.Resources>
    <ScrollView>
        <VerticalStackLayout Padding="24" Spacing="16">
            <Label Text="WindowStream Server" FontSize="28" FontAttributes="Bold" />

            <!-- Top-level state board -->
            <Grid ColumnDefinitions="Auto,Auto,*" RowDefinitions="Auto,Auto,Auto,Auto" RowSpacing="6" ColumnSpacing="12">
                <Label Grid.Row="0" Grid.Column="0" Text="{Binding State.Listening, Converter={StaticResource GlyphConverter}}" FontSize="18" />
                <Label Grid.Row="0" Grid.Column="1" Text="Listening" FontAttributes="Bold" />
                <Label Grid.Row="0" Grid.Column="2" Text="{Binding TcpPort, StringFormat='TCP {0}'}"  />
                <Label Grid.Row="1" Grid.Column="0" Text="{Binding State.ViewerConnected, Converter={StaticResource GlyphConverter}}" FontSize="18" />
                <Label Grid.Row="1" Grid.Column="1" Text="Viewer" FontAttributes="Bold" />
                <Label Grid.Row="1" Grid.Column="2" Text="{Binding ConnectedViewer, TargetNullValue='not connected'}" />
                <Label Grid.Row="2" Grid.Column="0" Text="✓" FontSize="18" />
                <Label Grid.Row="2" Grid.Column="1" Text="Windows" FontAttributes="Bold" />
                <Label Grid.Row="2" Grid.Column="2" Text="{Binding AvailableWindowCount}" />
                <Label Grid.Row="3" Grid.Column="0" Text="…" FontSize="18" />
                <Label Grid.Row="3" Grid.Column="1" Text="Streams" FontAttributes="Bold" />
                <Label Grid.Row="3" Grid.Column="2" Text="{Binding ActiveStreamCount}" />
            </Grid>

            <!-- Per-stream rows -->
            <Label Text="Active streams" FontAttributes="Bold" Margin="0,12,0,0" />
            <CollectionView ItemsSource="{Binding State.Streams.Values}">
                <CollectionView.ItemTemplate>
                    <DataTemplate x:DataType="{x:Type pages:StreamStateRow}">
                        <Border StrokeThickness="1" Stroke="#333" Padding="8" Margin="0,2">
                            <VerticalStackLayout>
                                <Label Text="{Binding Title, StringFormat='windowId / {0}'}" FontAttributes="Bold" />
                                <Grid ColumnDefinitions="Auto,Auto,*" RowDefinitions="Auto,Auto,Auto,Auto" RowSpacing="2" ColumnSpacing="8">
                                    <Label Grid.Row="0" Grid.Column="0" Text="{Binding WorkerSpawn, Converter={StaticResource GlyphConverter}}" />
                                    <Label Grid.Row="0" Grid.Column="1" Text="Worker spawn" />
                                    <Label Grid.Row="0" Grid.Column="2" Text="{Binding WorkerSpawnError, TargetNullValue=''}" />
                                    <Label Grid.Row="1" Grid.Column="0" Text="{Binding Capture, Converter={StaticResource GlyphConverter}}" />
                                    <Label Grid.Row="1" Grid.Column="1" Text="Capture" />
                                    <Label Grid.Row="1" Grid.Column="2" Text="{Binding CaptureError, TargetNullValue=''}" />
                                    <Label Grid.Row="2" Grid.Column="0" Text="{Binding Encode, Converter={StaticResource GlyphConverter}}" />
                                    <Label Grid.Row="2" Grid.Column="1" Text="Encode" />
                                    <Label Grid.Row="2" Grid.Column="2" Text="{Binding EncodeError, TargetNullValue=''}" />
                                    <Label Grid.Row="3" Grid.Column="0" Text="{Binding UdpSend, Converter={StaticResource GlyphConverter}}" />
                                    <Label Grid.Row="3" Grid.Column="1" Text="UDP send" />
                                    <Label Grid.Row="3" Grid.Column="2" Text="{Binding CurrentFps, StringFormat='{0:0.0} fps', TargetNullValue=''}" />
                                </Grid>
                            </VerticalStackLayout>
                        </Border>
                    </DataTemplate>
                </CollectionView.ItemTemplate>
            </CollectionView>

            <!-- Event log pane -->
            <Grid ColumnDefinitions="*,Auto" Margin="0,12,0,0">
                <Label Grid.Column="0" Text="Recent events" FontAttributes="Bold" VerticalOptions="Center" />
                <Button Grid.Column="1" Text="Open log folder" Clicked="OnOpenLogFolderClicked" />
            </Grid>
            <CollectionView ItemsSource="{Binding RecentEvents}" HeightRequest="320">
                <CollectionView.ItemTemplate>
                    <DataTemplate x:DataType="viewModels:LogEntryViewModel">
                        <Grid ColumnDefinitions="Auto,Auto,Auto,*" ColumnSpacing="8" Padding="2">
                            <Label Grid.Column="0" Text="{Binding Timestamp}" TextColor="#888" FontFamily="Consolas" />
                            <Label Grid.Column="1" Text="{Binding Severity}" TextColor="{Binding SeverityColor}" FontFamily="Consolas" />
                            <Label Grid.Column="2" Text="{Binding EventType}" FontAttributes="Bold" />
                            <Label Grid.Column="3" Text="{Binding Message}" />
                        </Grid>
                    </DataTemplate>
                </CollectionView.ItemTemplate>
            </CollectionView>
        </VerticalStackLayout>
    </ScrollView>
</ContentPage>
```

- [ ] **Step 3: Add the click handler in `MainPage.xaml.cs`**

In `src/WindowStreamServer/Pages/MainPage.xaml.cs`, add:
```csharp
private void OnOpenLogFolderClicked(object? sender, EventArgs e)
{
    string logsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowStream", "logs");
    Process.Start(new ProcessStartInfo
    {
        FileName = logsPath,
        UseShellExecute = true,
    });
}
```
And add `using System.Diagnostics;` and `using System.IO;`.

- [ ] **Step 4: Build + run smoke**

Run: `dotnet build src/WindowStreamServer/WindowStreamServer.csproj`
Expected: 0 errors. Launch with `dotnet run --project src/WindowStreamServer/ -f net10.0-windows10.0.19041.0` and verify the dashboard renders.

- [ ] **Step 5: Commit**

```bash
git add src/WindowStreamServer/Pages/MainPage.xaml \
        src/WindowStreamServer/Pages/MainPage.xaml.cs \
        src/WindowStreamServer/Pages/StageStatusGlyphConverter.cs
git commit -m "feat(server): state-board UI with per-stream rows and event log"
```

---

## Phase 4: Viewer foundation (Timber + types + trees)

### Task 11: Add Timber dependency

**Files:**
- Modify: `viewer/WindowStreamViewer/gradle/libs.versions.toml`
- Modify: `viewer/WindowStreamViewer/app/build.gradle.kts`

- [ ] **Step 1: Add `timber` entry to `libs.versions.toml`**

Add under `[versions]`:
```toml
timber = "5.0.1"
```
Add under `[libraries]`:
```toml
timber = { module = "com.jakewharton.timber:timber", version.ref = "timber" }
```

- [ ] **Step 2: Add dependency to `app/build.gradle.kts`**

In `dependencies { ... }`:
```kotlin
implementation(libs.timber)
```

- [ ] **Step 3: Sync + build**

Run: `./gradlew :app:assemblePortableDebug --no-configuration-cache`
Expected: BUILD SUCCESSFUL.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/gradle/libs.versions.toml \
        viewer/WindowStreamViewer/app/build.gradle.kts
git commit -m "build(viewer): add Timber 5.0.1 dependency"
```

### Task 12: Viewer `PipelineEvent` sealed class

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEvent.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEventTest.kt`

- [ ] **Step 1: Write the failing test**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class PipelineEventTest {
    @Test
    fun `DiscoveryResultReceived has info severity and carries fields`() {
        val event = PipelineEvent.DiscoveryResultReceived(
            hostname = "chonkers", address = "192.168.1.10", port = 53234
        )
        assertEquals(Severity.INFO, event.severity)
        assertEquals(null, event.streamId)
        assertEquals("chonkers", event.hostname)
    }

    @Test
    fun `DecoderFailed has error severity and stream id`() {
        val event = PipelineEvent.DecoderFailed(streamId = 7, cause = RuntimeException("nope"))
        assertEquals(Severity.ERROR, event.severity)
        assertEquals(7, event.streamId)
    }

    @Test
    fun `UdpStalled has warning severity`() {
        val event = PipelineEvent.UdpStalled(streamId = 1, gapMs = 3000L)
        assertEquals(Severity.WARNING, event.severity)
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*PipelineEventTest*"`
Expected: COMPILATION FAILED.

- [ ] **Step 3: Write `PipelineEvent.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

enum class Severity { INFO, WARNING, ERROR }

sealed class PipelineEvent(val severity: Severity, val streamId: Int?) {
    object DiscoveryStarted : PipelineEvent(Severity.INFO, null)
    data class DiscoveryResultReceived(val hostname: String, val address: String, val port: Int)
        : PipelineEvent(Severity.INFO, null)
    object DiscoveryTimedOut : PipelineEvent(Severity.WARNING, null)

    data class TcpConnecting(val host: String, val port: Int) : PipelineEvent(Severity.INFO, null)
    data class TcpConnected(val durationMs: Long) : PipelineEvent(Severity.INFO, null)
    data class TcpConnectFailed(val host: String, val port: Int, val cause: Throwable)
        : PipelineEvent(Severity.ERROR, null)

    data class ServerHelloReceived(val windowCount: Int, val udpPort: Int)
        : PipelineEvent(Severity.INFO, null)

    data class OpenStreamSent(val windowId: ULong) : PipelineEvent(Severity.INFO, null)
    data class StreamOpened(override val sid: Int, val width: Int, val height: Int)
        : PipelineEvent(Severity.INFO, sid) {
            companion object { /* shim placeholder */ }
        }
    data class StreamRefused(val sid: Int, val errorCode: String, val message: String)
        : PipelineEvent(Severity.WARNING, sid)
    data class StreamStopped(val sid: Int, val reason: String)
        : PipelineEvent(Severity.INFO, sid)

    data class UdpBound(val port: Int) : PipelineEvent(Severity.INFO, null)
    data class UdpFirstPacketReceived(val sid: Int, val delayMs: Long)
        : PipelineEvent(Severity.INFO, sid)
    data class UdpStalled(val sid: Int, val gapMs: Long)
        : PipelineEvent(Severity.WARNING, sid)

    data class DecoderStarting(val sid: Int, val width: Int, val height: Int)
        : PipelineEvent(Severity.INFO, sid)
    data class DecoderStarted(val sid: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderFailed(val sid: Int, val cause: Throwable)
        : PipelineEvent(Severity.ERROR, sid)

    data class SurfaceCreated(val panelIndex: Int) : PipelineEvent(Severity.INFO, null)
    data class SurfaceDestroyed(val panelIndex: Int, val reasonHint: String)
        : PipelineEvent(Severity.INFO, null)

    data class FramesPresenting(val sid: Int, val fps: Double) : PipelineEvent(Severity.INFO, sid)

    object WifiLockAcquired : PipelineEvent(Severity.INFO, null)
    object WifiLockReleased : PipelineEvent(Severity.INFO, null)

    val streamId_alias: Int? get() = streamId  // ergonomic helper; not necessary
}
```

**Important:** the constructor convention is `(severity, streamId)`. Two ways to handle event types that have a stream id:
- Pass `streamId` directly as the second positional arg (preferred — keeps the property `streamId` on the base class).
- The `sid` naming above shadows; rename `sid` → `streamId` and remove the `override`/`companion object` boilerplate. Final form:

```kotlin
data class StreamOpened(val sid: Int, val width: Int, val height: Int)
    : PipelineEvent(Severity.INFO, sid)
```
(All `data class` cases that have a stream id use `val sid: Int` as the first ctor param and pass it as the second arg to the superclass.)

Rewrite `PipelineEvent.kt` accordingly — final clean version:

```kotlin
package com.mtschoen.windowstream.viewer.observability

enum class Severity { INFO, WARNING, ERROR }

sealed class PipelineEvent(val severity: Severity, val streamId: Int?) {
    object DiscoveryStarted : PipelineEvent(Severity.INFO, null)
    data class DiscoveryResultReceived(val hostname: String, val address: String, val port: Int)
        : PipelineEvent(Severity.INFO, null)
    object DiscoveryTimedOut : PipelineEvent(Severity.WARNING, null)

    data class TcpConnecting(val host: String, val port: Int) : PipelineEvent(Severity.INFO, null)
    data class TcpConnected(val durationMs: Long) : PipelineEvent(Severity.INFO, null)
    data class TcpConnectFailed(val host: String, val port: Int, val cause: Throwable)
        : PipelineEvent(Severity.ERROR, null)

    data class ServerHelloReceived(val windowCount: Int, val udpPort: Int)
        : PipelineEvent(Severity.INFO, null)

    data class OpenStreamSent(val windowId: ULong) : PipelineEvent(Severity.INFO, null)
    data class StreamOpened(val sid: Int, val width: Int, val height: Int) : PipelineEvent(Severity.INFO, sid)
    data class StreamRefused(val sid: Int, val errorCode: String, val message: String) : PipelineEvent(Severity.WARNING, sid)
    data class StreamStopped(val sid: Int, val reason: String) : PipelineEvent(Severity.INFO, sid)

    data class UdpBound(val port: Int) : PipelineEvent(Severity.INFO, null)
    data class UdpFirstPacketReceived(val sid: Int, val delayMs: Long) : PipelineEvent(Severity.INFO, sid)
    data class UdpStalled(val sid: Int, val gapMs: Long) : PipelineEvent(Severity.WARNING, sid)

    data class DecoderStarting(val sid: Int, val width: Int, val height: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderStarted(val sid: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderFailed(val sid: Int, val cause: Throwable) : PipelineEvent(Severity.ERROR, sid)

    data class SurfaceCreated(val panelIndex: Int) : PipelineEvent(Severity.INFO, null)
    data class SurfaceDestroyed(val panelIndex: Int, val reasonHint: String) : PipelineEvent(Severity.INFO, null)

    data class FramesPresenting(val sid: Int, val fps: Double) : PipelineEvent(Severity.INFO, sid)

    object WifiLockAcquired : PipelineEvent(Severity.INFO, null)
    object WifiLockReleased : PipelineEvent(Severity.INFO, null)
}
```

Update the test's `DecoderFailed(streamId = 7, ...)` to `DecoderFailed(sid = 7, ...)`, etc.

- [ ] **Step 4: Run, verify PASS**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*PipelineEventTest*"`
Expected: PASS 3/3.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEvent.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/PipelineEventTest.kt
git commit -m "feat(viewer): add PipelineEvent sealed hierarchy and Severity enum"
```

### Task 13: `Diagnostics` object + `LogEvent` record + thread-local payload bridge

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/LogEvent.kt`
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/Diagnostics.kt`

- [ ] **Step 1: Write `LogEvent.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import java.time.Instant

data class LogEvent(
    val timestamp: Instant,
    val severity: Severity,
    val eventType: String,
    val streamId: Int?,
    val message: String,
    val payload: Map<String, Any?>,
    val throwable: Throwable?,
)
```

- [ ] **Step 2: Write `Diagnostics.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import timber.log.Timber
import java.time.Instant

/**
 * Façade that translates a [PipelineEvent] into a Timber call. Two custom
 * trees (FileLoggingTree, InAppBufferTree) read the payload via a
 * ThreadLocal map populated immediately before the log call.
 *
 * Per-frame markers ([FRAMECOUNT]) deliberately bypass this façade — they
 * live in stderr/logcat to avoid flooding the in-app buffer.
 */
object Diagnostics {

    internal val currentPayload: ThreadLocal<Map<String, Any?>> = ThreadLocal.withInitial { emptyMap() }
    internal val currentEvent: ThreadLocal<PipelineEvent?> = ThreadLocal.withInitial { null }

    fun report(event: PipelineEvent) {
        val tree = Timber.tag(TAG)
        val payload = payloadOf(event)
        currentPayload.set(payload)
        currentEvent.set(event)
        try {
            val message = describe(event)
            when (event.severity) {
                Severity.INFO -> tree.i(message)
                Severity.WARNING -> tree.w(message)
                Severity.ERROR -> tree.e(throwableOf(event), message)
            }
        } finally {
            currentPayload.remove()
            currentEvent.remove()
        }
    }

    private fun describe(event: PipelineEvent): String = event::class.simpleName + ": " + event.toString()

    private fun throwableOf(event: PipelineEvent): Throwable? = when (event) {
        is PipelineEvent.TcpConnectFailed -> event.cause
        is PipelineEvent.DecoderFailed -> event.cause
        else -> null
    }

    private fun payloadOf(event: PipelineEvent): Map<String, Any?> = buildMap {
        put("eventType", event::class.simpleName)
        put("streamId", event.streamId)
        when (event) {
            is PipelineEvent.DiscoveryResultReceived -> {
                put("hostname", event.hostname); put("address", event.address); put("port", event.port)
            }
            is PipelineEvent.TcpConnecting -> { put("host", event.host); put("port", event.port) }
            is PipelineEvent.TcpConnected -> put("durationMs", event.durationMs)
            is PipelineEvent.TcpConnectFailed -> { put("host", event.host); put("port", event.port) }
            is PipelineEvent.ServerHelloReceived -> {
                put("windowCount", event.windowCount); put("udpPort", event.udpPort)
            }
            is PipelineEvent.OpenStreamSent -> put("windowId", event.windowId.toString())
            is PipelineEvent.StreamOpened -> { put("width", event.width); put("height", event.height) }
            is PipelineEvent.StreamRefused -> { put("errorCode", event.errorCode); put("message", event.message) }
            is PipelineEvent.StreamStopped -> put("reason", event.reason)
            is PipelineEvent.UdpBound -> put("port", event.port)
            is PipelineEvent.UdpFirstPacketReceived -> put("delayMs", event.delayMs)
            is PipelineEvent.UdpStalled -> put("gapMs", event.gapMs)
            is PipelineEvent.DecoderStarting -> { put("width", event.width); put("height", event.height) }
            is PipelineEvent.SurfaceCreated -> put("panelIndex", event.panelIndex)
            is PipelineEvent.SurfaceDestroyed -> {
                put("panelIndex", event.panelIndex); put("reasonHint", event.reasonHint)
            }
            is PipelineEvent.FramesPresenting -> put("fps", event.fps)
            else -> {} // objects + types without extra payload
        }
    }

    private const val TAG = "Pipeline"
}
```

- [ ] **Step 3: Build + smoke test**

Run: `./gradlew :app:assemblePortableDebug` — expect SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/
git commit -m "feat(viewer): Diagnostics façade with ThreadLocal payload bridge"
```

### Task 14: `InAppBufferTree`

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTree.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTreeTest.kt`

- [ ] **Step 1: Write the failing test**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import timber.log.Timber

class InAppBufferTreeTest {

    @Test
    fun `report emits one LogEvent on the SharedFlow`() = runBlocking {
        val tree = InAppBufferTree(replay = 16)
        Timber.plant(tree)
        try {
            Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
            val received = tree.events.first()
            assertEquals("DiscoveryTimedOut", received.eventType)
            assertEquals(Severity.WARNING, received.severity)
        } finally {
            Timber.uproot(tree)
        }
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*InAppBufferTreeTest*"`
Expected: FAIL — `InAppBufferTree` missing.

- [ ] **Step 3: Write `InAppBufferTree.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import timber.log.Timber
import java.time.Instant

class InAppBufferTree(replay: Int = 200) : Timber.Tree() {

    private val _events = MutableSharedFlow<LogEvent>(replay = replay, extraBufferCapacity = 64)
    val events: SharedFlow<LogEvent> = _events.asSharedFlow()

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        val event = Diagnostics.currentEvent.get()
        val payload = Diagnostics.currentPayload.get()
        val severity = when {
            priority >= android.util.Log.ERROR -> Severity.ERROR
            priority >= android.util.Log.WARN -> Severity.WARNING
            else -> Severity.INFO
        }
        val logEvent = LogEvent(
            timestamp = Instant.now(),
            severity = severity,
            eventType = (payload["eventType"] as? String) ?: "Log",
            streamId = payload["streamId"] as? Int,
            message = message,
            payload = payload,
            throwable = t,
        )
        _events.tryEmit(logEvent)
    }
}
```

- [ ] **Step 4: Run, verify PASS**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*InAppBufferTreeTest*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTree.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTreeTest.kt
git commit -m "feat(viewer): InAppBufferTree exposing SharedFlow of LogEvent"
```

### Task 15: `FileLoggingTree`

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTree.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTreeTest.kt`

- [ ] **Step 1: Write the failing test (Robolectric-free, uses a temp dir directly)**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import timber.log.Timber
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.ZoneOffset

class FileLoggingTreeTest {

    @Test
    fun `report writes one JSONL line to dated file`(@TempDir tempDir: File) {
        val clock = Clock.fixed(Instant.parse("2026-05-17T12:34:56Z"), ZoneOffset.UTC)
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clock)
        Timber.plant(tree)
        try {
            Diagnostics.report(PipelineEvent.UdpBound(port = 53235))
            tree.flush()
            val expected = File(tempDir, "viewer-2026-05-17.jsonl")
            assertTrue(expected.exists())
            val lines = expected.readLines()
            assertEquals(1, lines.size)
            assertTrue(lines[0].contains("\"eventType\":\"UdpBound\""))
        } finally {
            Timber.uproot(tree)
            tree.close()
        }
    }

    @Test
    fun `rotation deletes files older than retentionDays`(@TempDir tempDir: File) {
        // create stale files
        File(tempDir, "viewer-2026-05-09.jsonl").writeText("old\n")
        File(tempDir, "viewer-2026-05-10.jsonl").writeText("old\n")
        val clock = Clock.fixed(Instant.parse("2026-05-17T00:00:00Z"), ZoneOffset.UTC)
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clock)
        Timber.plant(tree)
        try {
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            tree.flush()
            assertTrue(!File(tempDir, "viewer-2026-05-09.jsonl").exists())
            assertTrue(File(tempDir, "viewer-2026-05-10.jsonl").exists()) // exactly retentionDays old, kept
        } finally {
            Timber.uproot(tree)
            tree.close()
        }
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*FileLoggingTreeTest*"`
Expected: FAIL — `FileLoggingTree` missing.

- [ ] **Step 3: Write `FileLoggingTree.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import timber.log.Timber
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.time.Clock
import java.time.Duration
import java.time.LocalDate
import java.time.ZoneId
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class FileLoggingTree(
    private val directory: File,
    private val retentionDays: Int = 7,
    private val clock: Clock = Clock.systemUTC(),
) : Timber.Tree(), AutoCloseable {

    private val executor: ExecutorService = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "WindowStream-Log-Writer").apply { isDaemon = true }
    }
    private var currentDate: LocalDate? = null
    private var writer: BufferedWriter? = null

    init {
        directory.mkdirs()
    }

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        val payload = Diagnostics.currentPayload.get()
        val severity = when {
            priority >= android.util.Log.ERROR -> "ERROR"
            priority >= android.util.Log.WARN -> "WARN"
            else -> "INFO"
        }
        val nowInstant = clock.instant()
        val nowDate = nowInstant.atZone(ZoneId.from(ZoneOffsetUtc)).toLocalDate()

        val record = buildJsonObject {
            put("ts", nowInstant.toString())
            put("level", severity)
            put("eventType", (payload["eventType"] as? String) ?: "Log")
            payload["streamId"]?.let { put("streamId", it.toString()) }
            put("msg", message)
            t?.let { put("exception", it.stackTraceToString()) }
            for ((key, value) in payload) {
                if (key == "eventType" || key == "streamId") continue
                put(key, value?.toString() ?: "")
            }
        }
        val line = Json.encodeToString(JsonElement.serializer(), record)

        executor.execute {
            try {
                rotateIfNeeded(nowDate)
                writer?.appendLine(line)
            } catch (failure: Throwable) {
                android.util.Log.e("FileLoggingTree", "write failed", failure)
            }
        }
    }

    fun flush() {
        executor.submit { writer?.flush() }.get()
    }

    private fun rotateIfNeeded(today: LocalDate) {
        if (currentDate == today && writer != null) return
        writer?.close()
        currentDate = today
        val file = File(directory, "viewer-$today.jsonl")
        writer = BufferedWriter(FileWriter(file, /* append = */ true))
        purgeOldFiles(today)
    }

    private fun purgeOldFiles(today: LocalDate) {
        val cutoff = today.minusDays(retentionDays.toLong())
        directory.listFiles { _, name -> name.matches(Regex("""viewer-\d{4}-\d{2}-\d{2}\.jsonl""")) }
            ?.forEach { file ->
                val dateText = file.nameWithoutExtension.removePrefix("viewer-")
                val fileDate = runCatching { LocalDate.parse(dateText) }.getOrNull() ?: return@forEach
                if (fileDate.isBefore(cutoff)) file.delete()
            }
    }

    override fun close() {
        executor.submit { writer?.close() }.get()
        executor.shutdown()
    }

    private val ZoneOffsetUtc get() = java.time.ZoneOffset.UTC
}
```

- [ ] **Step 4: Run, verify PASS**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*FileLoggingTreeTest*"`
Expected: PASS 2/2. If serialization complains about generic `Any?` in `put(...)`, switch the loop to `put(key, JsonPrimitive(value?.toString()))`.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTree.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/FileLoggingTreeTest.kt
git commit -m "feat(viewer): FileLoggingTree with daily rotation and retention"
```

### Task 16: Plant trees in `WindowStreamViewerApplication`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/WindowStreamViewerApplication.kt`

- [ ] **Step 1: Read current Application class**

Use Read tool on the file path above.

- [ ] **Step 2: Update `onCreate` to plant trees**

```kotlin
package com.mtschoen.windowstream.viewer.app

import android.app.Application
import com.mtschoen.windowstream.viewer.observability.FileLoggingTree
import com.mtschoen.windowstream.viewer.observability.InAppBufferTree
import timber.log.Timber
import java.io.File

class WindowStreamViewerApplication : Application() {

    lateinit var inAppBufferTree: InAppBufferTree
        private set

    override fun onCreate() {
        super.onCreate()
        if (Timber.treeCount == 0) {
            Timber.plant(Timber.DebugTree())
            val logsDirectory = File(getExternalFilesDir(null), "logs")
            Timber.plant(FileLoggingTree(directory = logsDirectory))
            inAppBufferTree = InAppBufferTree(replay = 200)
            Timber.plant(inAppBufferTree)
        }
    }
}
```

- [ ] **Step 3: Build + install**

Run: `./gradlew :app:assemblePortableDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk`
Expected: BUILD SUCCESSFUL; install succeeds. Launch viewer, run `adb logcat | grep Pipeline` — should be empty until pipeline events are emitted.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/WindowStreamViewerApplication.kt
git commit -m "feat(viewer): plant FileLoggingTree + InAppBufferTree in Application"
```

---

## Phase 5: Viewer instrumentation (call sites → Diagnostics)

### Task 17: Refactor `UnifiedStreamingActivity` to emit `PipelineEvent`s

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt`

Replace existing `Log.i(TAG, …)` / `Log.e(TAG, …)` calls that mark pipeline stages with `Diagnostics.report(...)`. Keep `Log.i` / `Log.e` for purely free-form info (tab UI, soft keyboard) — those don't need typed events.

- [ ] **Step 1: Replace discovery + connect events**

In `discoverAndConnect()`, replace:
```kotlin
Log.i(TAG, "discovered ${server.hostname} at ${server.host.hostAddress}:${server.controlPort}")
```
with:
```kotlin
Diagnostics.report(PipelineEvent.DiscoveryResultReceived(
    hostname = server.hostname,
    address = server.host.hostAddress ?: "?",
    port = server.controlPort))
```

Add at the start of `discoverAndConnect()`:
```kotlin
Diagnostics.report(PipelineEvent.DiscoveryStarted)
```

Wrap the `withTimeout(30_000)` in a `try`/`catch (TimeoutCancellationException)` and report `DiscoveryTimedOut` (also keep the existing catch for general throwables, where we already log).

Replace:
```kotlin
Log.e(TAG, "discovery/connect failed", throwable)
```
with:
```kotlin
Diagnostics.report(PipelineEvent.TcpConnectFailed(host = host, port = port, cause = throwable))
```

- [ ] **Step 2: Replace ServerHello + open + lifecycle**

In `connectToServer`, around `client.connect(...)`:
```kotlin
val connectStart = System.nanoTime()
Diagnostics.report(PipelineEvent.TcpConnecting(host = host, port = port))
val liveConnection = client.connect(activityScope)
val elapsedMs = (System.nanoTime() - connectStart) / 1_000_000
Diagnostics.report(PipelineEvent.TcpConnected(durationMs = elapsedMs))
```

Replace `Log.i(TAG, "connected: ${initialCatalogue.size} window(s) advertised")` with:
```kotlin
Diagnostics.report(PipelineEvent.ServerHelloReceived(
    windowCount = initialCatalogue.size,
    udpPort = liveConnection.serverHello.udpPort))
```

In `openWindow`, replace `Log.i(TAG, "opening stream for windowId=$windowId")`:
```kotlin
Diagnostics.report(PipelineEvent.OpenStreamSent(windowId = windowId))
```

For the `StreamLifecycleEvent` collector branches:
- `Opened` → `Diagnostics.report(PipelineEvent.StreamOpened(event.streamId, event.width, event.height))`
- `Refused` → `Diagnostics.report(PipelineEvent.StreamRefused(event.streamId, event.errorCode, event.message))`
- `Stopped` → `Diagnostics.report(PipelineEvent.StreamStopped(event.streamId, event.reason.reason))`

Keep the existing `runOnUiThread { statusLabel.text = ... }` UI updates.

- [ ] **Step 3: Surface lifecycle**

In `createSurfaceCallback`:
- `surfaceCreated`: `Diagnostics.report(PipelineEvent.SurfaceCreated(panelIndex))`
- `surfaceDestroyed`: `Diagnostics.report(PipelineEvent.SurfaceDestroyed(panelIndex, reasonHint = "lifecycle"))`

In `acquireWifiLock`:
```kotlin
Diagnostics.report(PipelineEvent.WifiLockAcquired)
```
And in `onDestroy` where the lock is released:
```kotlin
Diagnostics.report(PipelineEvent.WifiLockReleased)
```

- [ ] **Step 4: UDP arrival tracking**

In `startDecoderLocked`, after `udpReceiver.start(pipelineScope)`, but before kicking the decoder, attach a `Flow` operator that emits `UdpFirstPacketReceived` on first packet:
```kotlin
val openInstantNanos = System.nanoTime()
var firstReported = false
val instrumentedFrames: Flow<EncodedFrame> = frames.onEach {
    if (!firstReported) {
        firstReported = true
        val delay = (System.nanoTime() - openInstantNanos) / 1_000_000
        Diagnostics.report(PipelineEvent.UdpFirstPacketReceived(streamId, delay))
    }
}
Diagnostics.report(PipelineEvent.UdpBound(udpReceiver.boundPort))
Diagnostics.report(PipelineEvent.DecoderStarting(streamId, resolvedWidth, resolvedHeight))
```

Replace the rest of the body to use `instrumentedFrames` instead of `frames` for the `decoder.start(...)` call. Add the import: `import kotlinx.coroutines.flow.onEach`.

After `decoder.start(...)`:
```kotlin
Diagnostics.report(PipelineEvent.DecoderStarted(streamId))
```

Wrap `decoder.start(...)` in a `try` that catches and emits `DecoderFailed`.

- [ ] **Step 5: Build + smoke install**

Run: `./gradlew :app:assemblePortableDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk`
Expected: BUILD SUCCESSFUL. Launch viewer; run `adb logcat -s Pipeline:V` and exercise: open viewer → expect `DiscoveryStarted` then `DiscoveryResultReceived` or `DiscoveryTimedOut`.

- [ ] **Step 6: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt
git commit -m "refactor(viewer): emit PipelineEvents from UnifiedStreamingActivity"
```

### Task 18: Refactor `XrDemoActivity` + GXR `MainActivity`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt`

- [ ] **Step 1: `XrDemoActivity` — apply same patterns as Task 17**

For each existing `Log.i(TAG, "...")` that maps to a pipeline stage:
- "starting XR compositor path" → unchanged (free-form)
- "SpatialExternalSurface created" → `SurfaceCreated(panelIndex = 0)`
- "SpatialExternalSurface destroyed" → `SurfaceDestroyed(panelIndex = 0, "spatial-lifecycle")`
- "TCP connected to" → `TcpConnected(durationMs = measuredMs)` (add timing)
- "ServerHello: N window(s)" → `ServerHelloReceived(serverHello.windows.size, serverHello.udpPort)`
- "opening windowId=$windowId" → `OpenStreamSent(windowId.toULong())`
- "StreamStarted: ${stream.width}x${stream.height} streamId=${stream.streamId}" → `StreamOpened(stream.streamId, stream.width, stream.height)`
- "UDP bound on port ${udpReceiver.boundPort}" → `UdpBound(udpReceiver.boundPort)`
- "decoder started, rendering through XR compositor" → `DecoderStarted(stream.streamId)` (after a `DecoderStarting`)

- [ ] **Step 2: `MainActivity` (GXR picker)**

Read the file in full first (`viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt`). Locate:
1. The mDNS discovery start — wrap with `Diagnostics.report(PipelineEvent.DiscoveryStarted)` immediately before, and `Diagnostics.report(PipelineEvent.DiscoveryResultReceived(...))` on each server result.
2. The discovery timeout branch — emit `DiscoveryTimedOut`.
3. The window-selection handler (the picker handoff that fires the Intent to `XrDemoActivity`) — emit `Diagnostics.report(PipelineEvent.OpenStreamSent(windowId))` before `startActivity(intent)`.

If `MainActivity` already delegates discovery to `NetworkServiceDiscoveryClient` shared with `UnifiedStreamingActivity`, the report sites are at the same call layer — copy the pattern from Task 17 Step 1 verbatim.

Do NOT commit without showing concrete diffs of all three insertion points.

- [ ] **Step 3: Build all flavors**

Run: `./gradlew :app:assembleDebug`
Expected: BUILD SUCCESSFUL for both `portable` and `gxr`.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt
git commit -m "refactor(viewer): emit PipelineEvents from XrDemoActivity + GXR MainActivity"
```

### Task 19: `MediaCodecDecoder` + `MultiStreamControlClient` instrumentation

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/control/MultiStreamControlClient.kt`

- [ ] **Step 1: `MediaCodecDecoder` — wrap `start` and error paths**

Where the decoder configures and starts, wrap any error path with:
```kotlin
Diagnostics.report(PipelineEvent.DecoderFailed(streamId = /* threaded in or default 0 */, cause = exception))
```
Note: `MediaCodecDecoder` currently doesn't take a stream id. Add a `streamId: Int` constructor parameter and thread it through from `UnifiedStreamingActivity.startDecoderLocked` and `XrDemoActivity`'s decoder creation. Update both callsites.

- [ ] **Step 2: `MultiStreamControlClient` — wrap connect failures**

When `connect()` throws, emit `TcpConnectFailed`. When `StreamLifecycleEvent.Refused` is parsed, emit `StreamRefused` from inside the parser too — currently the activity catches it but instrumentation inside the client provides defense-in-depth.

Note: avoid double-emitting. Prefer single emission site per event; the activity-level `Refused` emit in Task 17 is canonical, so for the client, only emit if the connection-level error is distinct (e.g., framing parse failure).

- [ ] **Step 3: Build**

Run: `./gradlew :app:assembleDebug`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/decoder/MediaCodecDecoder.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/control/MultiStreamControlClient.kt
git commit -m "refactor(viewer): instrument decoder + control client"
```

### Task 20: `UdpStalled` watchdog

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt`

- [ ] **Step 1: Watchdog implementation**

Inside `startDecoderLocked` after instrumenting first-packet detection (Task 17 Step 4), launch a watchdog:
```kotlin
pipelineScope.launch {
    delay(2000)
    if (!firstReported) {
        Diagnostics.report(PipelineEvent.UdpStalled(streamId, 2000))
    }
}
```
Note: `firstReported` is captured by lambda, must be `var` — adjust the declaration to `@Volatile var firstReported = false` or wrap in `AtomicBoolean`. Use `AtomicBoolean` for thread safety.

Rewrite the watchdog + first-packet flag using `AtomicBoolean`:
```kotlin
val firstReportedFlag = java.util.concurrent.atomic.AtomicBoolean(false)
val instrumentedFrames = frames.onEach {
    if (firstReportedFlag.compareAndSet(false, true)) {
        val delay = (System.nanoTime() - openInstantNanos) / 1_000_000
        Diagnostics.report(PipelineEvent.UdpFirstPacketReceived(streamId, delay))
    }
}
pipelineScope.launch {
    delay(2000)
    if (!firstReportedFlag.get()) {
        Diagnostics.report(PipelineEvent.UdpStalled(streamId, 2000))
    }
}
```

- [ ] **Step 2: Apply same pattern in `XrDemoActivity`**

Replicate near where `udpReceiver.start(...)` is invoked in `XrDemoActivity`.

- [ ] **Step 3: Build**

Run: `./gradlew :app:assembleDebug`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/
git commit -m "feat(viewer): UdpStalled 2s watchdog"
```

---

## Phase 6: Viewer observability UI

### Task 21: Viewer state reducer

**Files:**
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducer.kt`
- Create: `viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducerTest.kt`

- [ ] **Step 1: Write failing test**

```kotlin
package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class ViewerStateReducerTest {
    @Test
    fun `initial state is all Pending`() {
        val reducer = ViewerStateReducer()
        assertEquals(StageStatus.Pending, reducer.state.discovery)
        assertEquals(StageStatus.Pending, reducer.state.tcpConnect)
    }

    @Test
    fun `DiscoveryResultReceived sets discovery Ok`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.DiscoveryResultReceived("chonkers", "192.168.1.10", 53234))
        assertEquals(StageStatus.Ok, reducer.state.discovery)
    }

    @Test
    fun `StreamRefused on open stream flips openStream to Error`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamRefused(sid = 1, errorCode = "WGC_FAIL", message = "WGC E_FAIL"))
        assertEquals(StageStatus.Error, reducer.state.streams[1]?.openStream)
        assertEquals("WGC E_FAIL", reducer.state.streams[1]?.openStreamError)
    }
}
```

- [ ] **Step 2: Run, verify FAIL**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*ViewerStateReducerTest*"`
Expected: FAIL.

- [ ] **Step 3: Write `ViewerStateReducer.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.observability

enum class StageStatus { Pending, InProgress, Ok, Warning, Error }

data class StreamRowState(
    val openStream: StageStatus = StageStatus.Pending,
    val openStreamError: String? = null,
    val udpArriving: StageStatus = StageStatus.Pending,
    val udpFirstDelayMs: Long? = null,
    val decoder: StageStatus = StageStatus.Pending,
    val decoderError: String? = null,
    val presenting: StageStatus = StageStatus.Pending,
    val fps: Double? = null,
)

data class ViewerState(
    val discovery: StageStatus = StageStatus.Pending,
    val discoveredServer: String? = null,
    val tcpConnect: StageStatus = StageStatus.Pending,
    val tcpConnectError: String? = null,
    val serverHello: StageStatus = StageStatus.Pending,
    val windowCount: Int = 0,
    val streams: Map<Int, StreamRowState> = emptyMap(),
)

class ViewerStateReducer {
    var state: ViewerState = ViewerState()
        private set

    fun apply(event: PipelineEvent) {
        state = when (event) {
            is PipelineEvent.DiscoveryStarted -> state.copy(discovery = StageStatus.InProgress)
            is PipelineEvent.DiscoveryResultReceived -> state.copy(
                discovery = StageStatus.Ok,
                discoveredServer = "${event.hostname} (${event.address}:${event.port})",
            )
            is PipelineEvent.DiscoveryTimedOut -> state.copy(discovery = StageStatus.Warning)
            is PipelineEvent.TcpConnecting -> state.copy(tcpConnect = StageStatus.InProgress)
            is PipelineEvent.TcpConnected -> state.copy(tcpConnect = StageStatus.Ok)
            is PipelineEvent.TcpConnectFailed -> state.copy(
                tcpConnect = StageStatus.Error,
                tcpConnectError = event.cause.message,
            )
            is PipelineEvent.ServerHelloReceived -> state.copy(
                serverHello = StageStatus.Ok,
                windowCount = event.windowCount,
            )
            is PipelineEvent.OpenStreamSent -> state.copy(
                // we don't yet have a streamId until StreamOpened, so attach to placeholder key -1
                streams = state.streams + (-1 to (state.streams[-1] ?: StreamRowState()).copy(
                    openStream = StageStatus.InProgress,
                )),
            )
            is PipelineEvent.StreamOpened -> state.copy(
                streams = (state.streams - (-1)) + (event.sid to StreamRowState(openStream = StageStatus.Ok)),
            )
            is PipelineEvent.StreamRefused -> {
                val existing = state.streams[event.sid] ?: state.streams[-1] ?: StreamRowState()
                state.copy(streams = (state.streams - (-1)) + (event.sid to existing.copy(
                    openStream = StageStatus.Error,
                    openStreamError = event.message,
                )))
            }
            is PipelineEvent.StreamStopped -> state.copy(streams = state.streams - event.sid)
            is PipelineEvent.UdpFirstPacketReceived -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    udpArriving = StageStatus.Ok,
                    udpFirstDelayMs = event.delayMs,
                )))
            }
            is PipelineEvent.UdpStalled -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(udpArriving = StageStatus.Warning)))
            }
            is PipelineEvent.DecoderStarted -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(decoder = StageStatus.Ok)))
            }
            is PipelineEvent.DecoderFailed -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    decoder = StageStatus.Error,
                    decoderError = event.cause.message,
                )))
            }
            is PipelineEvent.FramesPresenting -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    presenting = StageStatus.Ok,
                    fps = event.fps,
                )))
            }
            else -> state
        }
    }
}
```

- [ ] **Step 4: Run, verify PASS**

Run: `./gradlew :app:testPortableDebugUnitTest --tests "*ViewerStateReducerTest*"`
Expected: PASS 3/3.

- [ ] **Step 5: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducer.kt \
        viewer/WindowStreamViewer/app/src/test/kotlin/com/mtschoen/windowstream/viewer/observability/ViewerStateReducerTest.kt
git commit -m "feat(viewer): state reducer for observability board"
```

### Task 22: Phone/tablet overlay panel in `UnifiedStreamingActivity`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt`
- Create: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/ObservabilityOverlay.kt`

- [ ] **Step 1: Write `ObservabilityOverlay.kt`**

```kotlin
package com.mtschoen.windowstream.viewer.demo

import android.content.Context
import android.graphics.Color
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.mtschoen.windowstream.viewer.observability.LogEvent
import com.mtschoen.windowstream.viewer.observability.Severity
import com.mtschoen.windowstream.viewer.observability.StageStatus
import com.mtschoen.windowstream.viewer.observability.ViewerState

class ObservabilityOverlay(context: Context) {

    private val statusLines: LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(24, 24, 24, 24)
    }
    private val eventLogContainer: LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(24, 0, 24, 24)
    }
    private val eventLogScroll: ScrollView = ScrollView(context).apply {
        addView(eventLogContainer)
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f
        )
    }
    val rootView: FrameLayout = FrameLayout(context).apply {
        setBackgroundColor(Color.argb(220, 0, 0, 0))
        visibility = View.GONE
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
            addView(statusLines)
            addView(eventLogScroll)
        })
    }

    fun show() { rootView.visibility = View.VISIBLE }
    fun hide() { rootView.visibility = View.GONE }
    fun toggle() { if (rootView.visibility == View.VISIBLE) hide() else show() }

    fun renderState(state: ViewerState) {
        statusLines.removeAllViews()
        addLine(state.discovery, "Discovery", state.discoveredServer ?: "")
        addLine(state.tcpConnect, "TCP connect", state.tcpConnectError ?: "")
        addLine(state.serverHello, "ServerHello", "${state.windowCount} window(s)")
        state.streams.forEach { (streamId, row) ->
            addLine(StageStatus.Ok, "Stream #$streamId", "")
            addLine(row.openStream, "  open", row.openStreamError ?: "")
            addLine(row.udpArriving, "  UDP", row.udpFirstDelayMs?.let { "first packet ${it}ms" } ?: "")
            addLine(row.decoder, "  decoder", row.decoderError ?: "")
            addLine(row.presenting, "  presenting", row.fps?.let { "%.1f fps".format(it) } ?: "")
        }
    }

    fun appendEvent(event: LogEvent) {
        val line = TextView(rootView.context).apply {
            textSize = 11f
            text = "%s %s %s %s".format(
                event.timestamp.toString().substringAfterLast(":").take(8),
                event.severity.name.take(1),
                event.eventType,
                event.message,
            )
            setTextColor(when (event.severity) {
                Severity.ERROR -> Color.rgb(255, 100, 100)
                Severity.WARNING -> Color.rgb(255, 200, 80)
                else -> Color.rgb(200, 200, 200)
            })
        }
        eventLogContainer.addView(line)
        while (eventLogContainer.childCount > 200) eventLogContainer.removeViewAt(0)
        eventLogScroll.post { eventLogScroll.fullScroll(View.FOCUS_DOWN) }
    }

    private fun addLine(status: StageStatus, label: String, detail: String) {
        val glyph = when (status) {
            StageStatus.Ok -> "✓"
            StageStatus.Warning -> "⚠"
            StageStatus.Error -> "✗"
            StageStatus.InProgress -> "…"
            else -> "—"
        }
        statusLines.addView(TextView(rootView.context).apply {
            text = "$glyph  $label  $detail"
            setTextColor(when (status) {
                StageStatus.Error -> Color.rgb(255, 100, 100)
                StageStatus.Warning -> Color.rgb(255, 200, 80)
                else -> Color.WHITE
            })
            textSize = 14f
        })
    }
}
```

- [ ] **Step 2: Wire into `UnifiedStreamingActivity`**

In `UnifiedStreamingActivity.buildLayout`, instantiate `ObservabilityOverlay` and add its `rootView` to the root `FrameLayout`. Add an "🛈" button to the tab bar that toggles the overlay.

Then in `onCreate` after `buildLayout()`:
```kotlin
val app = applicationContext as com.mtschoen.windowstream.viewer.app.WindowStreamViewerApplication
val reducer = com.mtschoen.windowstream.viewer.observability.ViewerStateReducer()
activityScope.launch {
    app.inAppBufferTree.events.collect { event ->
        val pipelineEvent = com.mtschoen.windowstream.viewer.observability.Diagnostics.currentEvent.get()
        if (pipelineEvent != null) reducer.apply(pipelineEvent)
        runOnUiThread {
            overlay.appendEvent(event)
            overlay.renderState(reducer.state)
        }
    }
}
```

**Note:** the ThreadLocal trick won't actually work across coroutine boundaries — the collector runs on a different thread than the report site. Fix by including the `PipelineEvent` in `LogEvent` itself: add a `val pipelineEvent: PipelineEvent? = null` field to `LogEvent`, populate it in `InAppBufferTree.log` from `Diagnostics.currentEvent.get()`, then the collector reads it directly.

**Refactor:** add `val pipelineEvent: PipelineEvent? = null` to `LogEvent.kt`, update `InAppBufferTree.log` to capture it, and remove the ThreadLocal lookup in the collector. The collector becomes:
```kotlin
app.inAppBufferTree.events.collect { event ->
    event.pipelineEvent?.let { reducer.apply(it) }
    runOnUiThread {
        overlay.appendEvent(event)
        overlay.renderState(reducer.state)
    }
}
```

Apply this refactor before building.

- [ ] **Step 3: Build + install + smoke**

Run: `./gradlew :app:assemblePortableDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/portable/debug/app-portable-debug.apk`
Expected: SUCCESS. Launch viewer, tap the "🛈" button — overlay opens with state board + event log populated.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/UnifiedStreamingActivity.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/ObservabilityOverlay.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/LogEvent.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/observability/InAppBufferTree.kt
git commit -m "feat(viewer): observability overlay panel in UnifiedStreamingActivity"
```

### Task 23: GXR `SpatialPanel` for `XrDemoActivity` + `MainActivity`

**Files:**
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt`
- Modify: `viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt`

- [ ] **Step 1: GXR — add a 2D `SpatialPanel` next to the streaming panel**

In `XrDemoActivity`, after the existing scene composition, add a second `SpatialPanel` (Jetpack XR scenecore API) hosting an `AndroidView { ObservabilityOverlay(context).rootView.apply { show() } }`. Anchor the panel to the right of the streaming panel using `SubspaceModifier.offset(x = …)`.

If the Jetpack XR scenecore API differs from what `XrDemoActivity` already uses (alpha13 vs alpha04), copy the existing panel-creation pattern from the same file and clone with adjusted offset + content.

Wire the `app.inAppBufferTree.events` collection identical to Task 22.

- [ ] **Step 2: GXR `MainActivity` — overlay (2D, not spatial)**

For `MainActivity`, which is the 2D picker before immersive: add the same `ObservabilityOverlay` overlay used in Task 22.

- [ ] **Step 3: Build + install GXR**

Run: `./gradlew :app:assembleGxrDebug && adb install -r viewer/WindowStreamViewer/app/build/outputs/apk/gxr/debug/app-gxr-debug.apk`
Expected: BUILD SUCCESSFUL.

- [ ] **Step 4: Commit**

```bash
git add viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/demo/XrDemoActivity.kt \
        viewer/WindowStreamViewer/app/src/main/kotlin/com/mtschoen/windowstream/viewer/app/MainActivity.kt
git commit -m "feat(viewer/gxr): observability SpatialPanel + 2D overlay in picker"
```

---

## Phase 7: Cleanup + documentation

### Task 24: Update `AGENTS.md` with diagnostics paths

**Files:**
- Modify: `AGENTS.md`

- [ ] **Step 1: Add a "Diagnostics" section to AGENTS.md**

After the "Debugging tips" section, add:

```markdown
### Diagnostics — pipeline state + JSONL logs

Both apps emit typed `PipelineEvent`s through a `Diagnostics` façade. State
boards and event logs live in-app; a rotating JSONL file log persists for
7 days.

**Server file log:** `%LOCALAPPDATA%\WindowStream\logs\server-YYYY-MM-DD.jsonl`.
Open via the dashboard's "Open log folder" button, or grep with `jq`:

```bash
jq 'select(.EventType=="WorkerSpawnFailed")' server-2026-05-17.jsonl
```

**Viewer file log:** `<app-external-files>/logs/viewer-YYYY-MM-DD.jsonl`.
Pull via `adb pull /storage/emulated/0/Android/data/com.mtschoen.windowstream.viewer/files/logs/`.

**What's NOT in the pipeline event stream:** `[FRAMECOUNT]` per-frame markers
stay on stderr / logcat — they would flood the in-app buffer + balloon the
file. The diagnostic boundary is *stage transitions and errors*, not
per-frame.
```

- [ ] **Step 2: Commit**

```bash
git add AGENTS.md
git commit -m "docs: diagnostics + log-file paths in AGENTS.md"
```

### Task 25: Final coverage check + Core tests for `Diagnostics.Subscribe` fan-out

**Files:**
- Modify: `tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs`

- [ ] **Step 1: Add subscribe test**

Append to `DiagnosticsTests.cs`:
```csharp
[Fact]
public void Subscribed_Handler_Receives_Event_After_Report()
{
    var loggerMock = new Mock<ILogger>();
    loggerMock.Setup(logger => logger.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    Diagnostics diagnostics = new(loggerMock.Object);

    PipelineEvent? received = null;
    diagnostics.Subscribe(evt => received = evt);

    PipelineEvent.Listening expected = new(53234, 53235);
    diagnostics.Report(expected);

    Assert.Same(expected, received);
}
```

- [ ] **Step 2: Run full test suite both sides**

Run:
```bash
dotnet test
./gradlew :app:testPortableDebugUnitTest
```
Expected: ALL PASS. Coverage gate (Core ≥ 100% line/branch, Kover green) holds.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Core.Tests/Observability/DiagnosticsTests.cs
git commit -m "test(core): cover Diagnostics.Subscribe fan-out"
```

### Task 26: End-to-end smoke test

- [ ] **Step 1: Server-side**

Run:
```bash
dotnet run --project src/WindowStreamServer -f net10.0-windows10.0.19041.0
```
Expected: dashboard opens. Verify the state board shows "Listening ✓" with ports populated. Open `%LOCALAPPDATA%\WindowStream\logs\` — see today's `.jsonl` file with at least the `Listening` event.

- [ ] **Step 2: Viewer-side (portable)**

Install + launch on a connected device. Tap "🛈" — overlay appears. Run:
```bash
adb pull /storage/emulated/0/Android/data/com.mtschoen.windowstream.viewer/files/logs/ ./tmp-viewer-logs/
```
Expected: at least `DiscoveryStarted` line in the JSONL.

- [ ] **Step 3: Fault-injection test**

Launch viewer with bogus selectedWindowIds (per existing `project_synthesize_window_not_found` pattern):
```bash
adb shell am start -n com.mtschoen.windowstream.viewer/.demo.UnifiedStreamingActivity \
    --es streamHost <pc-lan-ip> --ei streamPort <tcpPort> \
    --ela selectedWindowIds 99999999
```
Expected: overlay shows `Stream` row in error state with the server's `STREAM_REFUSED` message inline. Server dashboard shows the matching `StreamRefused` event in the event log.

- [ ] **Step 4: No commit (verification only)** — if anything fails, fix inline and commit.

---

## Final sanity

- [ ] Run `dotnet test` and `./gradlew :app:testPortableDebugUnitTest` — all green.
- [ ] Run `dotnet build` and `./gradlew :app:assembleDebug` — clean build both flavors.
- [ ] Confirm `git status` is clean.
- [ ] Confirm no `[FRAMECOUNT]` calls were accidentally routed through `Diagnostics` (grep for `Diagnostics.Report.*FRAMECOUNT` should be empty).
