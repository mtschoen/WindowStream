# M1 — Shared D3D11 Device Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the static `Direct3D11Helper.CreateDevice` with a disposable `Direct3D11DeviceManager` class that owns the native `ID3D11Device` + `ID3D11DeviceContext` + WinRT `IDirect3DDevice` together, and routes `WgcCaptureSource.Start` through it. Sets up the device-sharing primitive that M4 will hoist to share with NVENC.

**Architecture:** New `Direct3D11DeviceManager` (sealed, `IDisposable`) creates a D3D11 device with **both** `D3D11_CREATE_DEVICE_BGRA_SUPPORT` and `D3D11_CREATE_DEVICE_VIDEO_SUPPORT` flags set (the second is forward compatibility for M3's `VideoProcessorBlt`). It exposes the WinRT-wrapped device for WGC and the raw `ID3D11Device*` / `ID3D11DeviceContext*` pointers (as `nint`) for FFmpeg hwaccel and the M3 video processor. Per-capture lifetime in M1 (one manager per `WgcCapture`); promoted to per-worker scope in M4. **No behaviour change end-to-end** — same demo path, same frame pump, same `IWindowCapture` API.

**Tech Stack:** C# 12, .NET 8 + .NET 10 (Windows TFM), Silk.NET.Direct3D11 (already on project), xUnit, Coverlet. All new code is `#if WINDOWS`-guarded.

---

## File structure

**Create:**
- `src/WindowStream.Core/Capture/Windows/Direct3D11DeviceManager.cs` — the new class.
- `tests/WindowStream.Integration.Tests/Capture/Windows/Direct3D11DeviceManagerTests.cs` — integration tests proving the manager constructs a working device with video support and disposes cleanly.

**Modify:**
- `src/WindowStream.Core/Capture/Windows/WgcCapture.cs` — accept a `Direct3D11DeviceManager` (which owns the WinRT device); take ownership and dispose with the capture.
- `src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs` — replace `Direct3D11Helper.CreateDevice()` call with `new Direct3D11DeviceManager()`.

**Delete:**
- `src/WindowStream.Core/Capture/Windows/Direct3D11Helper.cs` — dead code after `WgcCaptureSource` switches to the manager. Its device-creation logic moves into `Direct3D11DeviceManager`.

**Untouched (verified):** `WgcFrameConverter.cs`, `FFmpegNvencEncoder.cs`, `WorkerCommandHandler.cs`, `CoordinatorLauncher.cs`, `MauiProgram.cs`, `CliServices.cs`, `ListWindowsCommandHandler.cs`, `WindowPickerViewModel.cs`. M4 will revisit the WorkerCommandHandler hosting site.

---

## Task 1: Add `Direct3D11DeviceManager` skeleton

Establishes the new class with the public surface that subsequent tasks will fill in. No real native call yet — just the shape.

**Files:**
- Create: `src/WindowStream.Core/Capture/Windows/Direct3D11DeviceManager.cs`

- [ ] **Step 1: Create the file with the public-API skeleton**

```csharp
#if WINDOWS
using System;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace WindowStream.Core.Capture.Windows;

/// <summary>
/// Owns a single <c>ID3D11Device</c> + <c>ID3D11DeviceContext</c> created
/// with BGRA + video support flags, and the matching WinRT
/// <see cref="WinRtDirect3DDevice"/> wrapper. Designed to be shared across
/// the capture pipeline (WGC consumes the WinRT wrapper; the M3 video
/// processor and the M4 FFmpeg hwaccel device context consume the raw
/// pointers). M1 lifetime is per-capture; M4 hoists this to per-worker
/// scope so the encoder and capture share a single device.
/// </summary>
public sealed class Direct3D11DeviceManager : IDisposable
{
    private bool disposed;
    private nint nativeDevicePointer;
    private nint nativeContextPointer;
    private WinRtDirect3DDevice? winRtDevice;

    public WinRtDirect3DDevice WinRtDevice =>
        winRtDevice ?? throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));

    public nint NativeDevicePointer
    {
        get
        {
            ThrowIfDisposed();
            return nativeDevicePointer;
        }
    }

    public nint NativeContextPointer
    {
        get
        {
            ThrowIfDisposed();
            return nativeContextPointer;
        }
    }

    public Direct3D11DeviceManager()
    {
        throw new NotImplementedException("Native device creation lands in Task 2.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Native release lands in Task 2.
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));
        }
    }
}
#endif
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: success, no errors.

- [ ] **Step 3: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows/Direct3D11DeviceManager.cs
git commit -m "feat(capture): add Direct3D11DeviceManager skeleton for M1"
```

---

## Task 2: Implement native device creation and disposal

Fills in the constructor and `Dispose()` with real D3D11 calls. Mirrors the existing `Direct3D11Helper.CreateDevice` logic but **adds the `D3D11_CREATE_DEVICE_VIDEO_SUPPORT` flag** (0x800) for M3's `ID3D11VideoDevice`/`VideoProcessorBlt` path. Test comes in Task 3 — implementation first because the constructor needs to exist before we can write a test that exercises it.

**Files:**
- Modify: `src/WindowStream.Core/Capture/Windows/Direct3D11DeviceManager.cs`

- [ ] **Step 1: Add the native imports and replace the constructor and `Dispose()` bodies**

Replace the entire file contents with:

```csharp
#if WINDOWS
using System;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using WinRT;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;

namespace WindowStream.Core.Capture.Windows;

/// <summary>
/// Owns a single <c>ID3D11Device</c> + <c>ID3D11DeviceContext</c> created
/// with BGRA + video support flags, and the matching WinRT
/// <see cref="WinRtDirect3DDevice"/> wrapper. Designed to be shared across
/// the capture pipeline (WGC consumes the WinRT wrapper; the M3 video
/// processor and the M4 FFmpeg hwaccel device context consume the raw
/// pointers). M1 lifetime is per-capture; M4 hoists this to per-worker
/// scope so the encoder and capture share a single device.
/// </summary>
public sealed class Direct3D11DeviceManager : IDisposable
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static readonly Guid iidIdxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private const uint DriverTypeHardware = 1;
    private const uint SdkVersion = 7;
    private const uint CreateBgraSupport = 0x20;
    private const uint CreateVideoSupport = 0x800;

    private bool disposed;
    private nint nativeDevicePointer;
    private nint nativeContextPointer;
    private WinRtDirect3DDevice? winRtDevice;

    public WinRtDirect3DDevice WinRtDevice =>
        winRtDevice ?? throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));

    public nint NativeDevicePointer
    {
        get
        {
            ThrowIfDisposed();
            return nativeDevicePointer;
        }
    }

    public nint NativeContextPointer
    {
        get
        {
            ThrowIfDisposed();
            return nativeContextPointer;
        }
    }

    public Direct3D11DeviceManager()
    {
        uint flags = CreateBgraSupport | CreateVideoSupport;
        int result = D3D11CreateDevice(
            adapter: IntPtr.Zero,
            driverType: DriverTypeHardware,
            software: IntPtr.Zero,
            flags: flags,
            featureLevels: IntPtr.Zero,
            featureLevelCount: 0,
            sdkVersion: SdkVersion,
            device: out IntPtr devicePointer,
            featureLevel: out _,
            immediateContext: out IntPtr contextPointer);

        if (result < 0)
        {
            throw new WindowCaptureException(
                "Failed to create D3D11 device. HRESULT: 0x"
                + ((uint)result).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        }

        try
        {
            unsafe
            {
                ID3D11Device* device = (ID3D11Device*)devicePointer;
                IDXGIDevice* dxgiDevice = null;
                Guid iid = iidIdxgiDevice;
                device->QueryInterface(ref iid, (void**)&dxgiDevice);

                int hr = CreateDirect3D11DeviceFromDXGIDevice((IntPtr)dxgiDevice, out IntPtr graphicsDevice);
                dxgiDevice->Release();

                if (hr < 0)
                {
                    throw new WindowCaptureException(
                        "Failed to create IDirect3DDevice wrapper. HRESULT: 0x"
                        + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                winRtDevice = MarshalInterface<WinRtDirect3DDevice>.FromAbi(graphicsDevice);
                Marshal.Release(graphicsDevice);
            }

            nativeDevicePointer = devicePointer;
            nativeContextPointer = contextPointer;
        }
        catch
        {
            Marshal.Release(devicePointer);
            Marshal.Release(contextPointer);
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        winRtDevice = null;

        if (nativeContextPointer != 0)
        {
            Marshal.Release(nativeContextPointer);
            nativeContextPointer = 0;
        }
        if (nativeDevicePointer != 0)
        {
            Marshal.Release(nativeDevicePointer);
            nativeDevicePointer = 0;
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));
        }
    }
}
#endif
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: success, no errors.

- [ ] **Step 3: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows/Direct3D11DeviceManager.cs
git commit -m "feat(capture): implement Direct3D11DeviceManager native device creation"
```

---

## Task 3: Add integration test for the manager

Verifies on real hardware that (a) construction succeeds with both flags, (b) the WinRT wrapper is present, (c) the raw device pointer can `QueryInterface` to `ID3D11VideoDevice` (proves the video flag actually took effect — this is the M3 prerequisite from the spec's Risks section), (d) disposal releases pointers cleanly without crashing.

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Capture/Windows/Direct3D11DeviceManagerTests.cs`

- [ ] **Step 1: Create the test file**

```csharp
#if WINDOWS
using System;
using Silk.NET.Direct3D11;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture.Windows;

[Trait("Category", "Windows")]
public sealed class Direct3D11DeviceManagerTests
{
    private static readonly Guid iidId3D11VideoDevice =
        new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");

    [Fact]
    public void CreateDefault_Constructs_With_NonNull_Pointers_And_WinRt_Wrapper()
    {
        using Direct3D11DeviceManager manager = new Direct3D11DeviceManager();

        Assert.NotEqual(IntPtr.Zero, manager.NativeDevicePointer);
        Assert.NotEqual(IntPtr.Zero, manager.NativeContextPointer);
        Assert.NotNull(manager.WinRtDevice);
    }

    [Fact]
    public void Native_Device_Supports_ID3D11VideoDevice_QueryInterface()
    {
        // Proves the D3D11_CREATE_DEVICE_VIDEO_SUPPORT flag took effect.
        // Required for M3's ID3D11VideoProcessor / VideoProcessorBlt path.
        using Direct3D11DeviceManager manager = new Direct3D11DeviceManager();

        unsafe
        {
            ID3D11Device* device = (ID3D11Device*)manager.NativeDevicePointer;
            ID3D11VideoDevice* videoDevice = null;
            Guid iid = iidId3D11VideoDevice;
            int hr = device->QueryInterface(ref iid, (void**)&videoDevice);
            try
            {
                Assert.True(hr >= 0, $"QueryInterface(ID3D11VideoDevice) failed: HRESULT 0x{(uint)hr:X8}");
                Assert.True(videoDevice != null);
            }
            finally
            {
                if (videoDevice != null) videoDevice->Release();
            }
        }
    }

    [Fact]
    public void Dispose_Is_Idempotent()
    {
        Direct3D11DeviceManager manager = new Direct3D11DeviceManager();
        manager.Dispose();
        manager.Dispose(); // must not throw
    }

    [Fact]
    public void After_Dispose_Property_Access_Throws()
    {
        Direct3D11DeviceManager manager = new Direct3D11DeviceManager();
        manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => manager.WinRtDevice);
        Assert.Throws<ObjectDisposedException>(() => manager.NativeDevicePointer);
        Assert.Throws<ObjectDisposedException>(() => manager.NativeContextPointer);
    }
}
#endif
```

- [ ] **Step 2: Run the new tests and verify they pass**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~Direct3D11DeviceManagerTests"`
Expected: all 4 tests pass. If `Native_Device_Supports_ID3D11VideoDevice_QueryInterface` fails with HRESULT 0x80070057 (`E_INVALIDARG`), the video flag did not take — re-check Task 2's flag construction.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Capture/Windows/Direct3D11DeviceManagerTests.cs
git commit -m "test(capture): integration tests for Direct3D11DeviceManager"
```

---

## Task 4: Wire `WgcCapture` to take and own the manager

Changes the WgcCapture constructor signature to accept a `Direct3D11DeviceManager` instead of an `IDirect3DDevice`. The capture takes ownership: it stores the manager, uses `manager.WinRtDevice` to create the framepool (matches today's behaviour), and disposes the manager when the capture is disposed. This gives us the per-capture lifetime the spec calls for in M1.

**Files:**
- Modify: `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`

- [ ] **Step 1: Replace the constructor signature, field, and `DisposeAsync` to take and dispose the manager**

Open `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`. Make the following three edits.

Edit 1 — replace the field and constructor parameter type. Find:

```csharp
    public WgcCapture(
        WindowHandle handle,
        CaptureOptions options,
        GraphicsCaptureItem item,
        IDirect3DDevice device,
        CancellationToken cancellationToken)
    {
        this.handle = handle;
        this.options = options;
        this.item = item;
        this.cancellationToken = cancellationToken;

        item.Closed += OnItemClosed;
        // Use CreateFreeThreaded so FrameArrived fires on any thread without a DispatcherQueue
        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: item.Size);
```

Replace with:

```csharp
    private readonly Direct3D11DeviceManager deviceManager;

    public WgcCapture(
        WindowHandle handle,
        CaptureOptions options,
        GraphicsCaptureItem item,
        Direct3D11DeviceManager deviceManager,
        CancellationToken cancellationToken)
    {
        this.handle = handle;
        this.options = options;
        this.item = item;
        this.deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        this.cancellationToken = cancellationToken;

        item.Closed += OnItemClosed;
        // Use CreateFreeThreaded so FrameArrived fires on any thread without a DispatcherQueue
        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            deviceManager.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: item.Size);
```

Edit 2 — extend `DisposeAsync` to dispose the manager. Find:

```csharp
    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }
        disposed = true;
        try { session.Dispose(); } catch { }
        try { framePool.Dispose(); } catch { }
        frameChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
```

Replace with:

```csharp
    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }
        disposed = true;
        try { session.Dispose(); } catch { }
        try { framePool.Dispose(); } catch { }
        try { deviceManager.Dispose(); } catch { }
        frameChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
```

- [ ] **Step 2: Verify the file compiles in isolation (will fail at the call site in `WgcCaptureSource` until Task 5)**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: ONE error, in `WgcCaptureSource.cs`, complaining that `WgcCapture` does not have a constructor accepting `IDirect3DDevice`. Task 5 fixes this. If you see other errors in `WgcCapture.cs` itself, re-check the edits.

- [ ] **Step 3: Do not commit yet** — the build is broken. Commit after Task 5 to keep history bisectable.

---

## Task 5: Wire `WgcCaptureSource.Start` to construct a manager

Replaces the `Direct3D11Helper.CreateDevice()` call with `new Direct3D11DeviceManager()`, hands the manager to `WgcCapture`. Build is restored after this edit.

**Files:**
- Modify: `src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs`

- [ ] **Step 1: Edit the `Start` method to build a manager and pass it through**

Open `src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs`. Find:

```csharp
    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new WindowCaptureException("Windows.Graphics.Capture is not supported on this OS build.");
        }

        GraphicsCaptureItem item = CreateItemForWindow(new IntPtr(handle.value), handle);
        WinRtDirect3DDevice device = Direct3D11Helper.CreateDevice();
        return new WgcCapture(handle, options, item, device, cancellationToken);
    }
```

Replace with:

```csharp
    public IWindowCapture Start(WindowHandle handle, CaptureOptions options, CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new WindowCaptureException("Windows.Graphics.Capture is not supported on this OS build.");
        }

        GraphicsCaptureItem item = CreateItemForWindow(new IntPtr(handle.value), handle);
        Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        try
        {
            return new WgcCapture(handle, options, item, deviceManager, cancellationToken);
        }
        catch
        {
            deviceManager.Dispose();
            throw;
        }
    }
```

- [ ] **Step 2: Remove the now-unused `WinRtDirect3DDevice` alias from the using directives**

In the same file, find:

```csharp
using WinRT;
using WinRtDirect3DDevice = Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice;
```

Replace with:

```csharp
using WinRT;
```

(`WinRT` is still needed for `MarshalInterface<T>.FromAbi`.)

- [ ] **Step 3: Verify the project compiles**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: success, no errors.

- [ ] **Step 4: Commit Tasks 4 + 5 together**

```bash
git add src/WindowStream.Core/Capture/Windows/WgcCapture.cs src/WindowStream.Core/Capture/Windows/WgcCaptureSource.cs
git commit -m "refactor(capture): route WgcCaptureSource through Direct3D11DeviceManager"
```

---

## Task 6: Delete the now-unused `Direct3D11Helper`

`Direct3D11Helper.CreateDevice()` had exactly one consumer (`WgcCaptureSource`), now using the manager instead. Delete the file.

**Files:**
- Delete: `src/WindowStream.Core/Capture/Windows/Direct3D11Helper.cs`

- [ ] **Step 1: Confirm no remaining references**

Run: `git grep "Direct3D11Helper"`
Expected: no matches (or only matches inside `docs/` referring to design notes — those don't need touching).

- [ ] **Step 2: Delete the file and verify the build**

```bash
git rm src/WindowStream.Core/Capture/Windows/Direct3D11Helper.cs
dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0
```
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git commit -m "chore(capture): remove dead Direct3D11Helper after manager refactor"
```

---

## Task 7: Run the existing WGC integration smoke test to confirm no regression

The pre-existing `WgcCaptureSourceSmokeTests.Attaches_To_Notepad_And_Receives_Frame` exercises the full path that we just refactored. It should pass with no test-side changes — proof that the manager refactor is behaviour-preserving.

- [ ] **Step 1: Run the smoke test**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~WgcCaptureSourceSmokeTests"`
Expected: 1 test passes. If it fails with `WindowCaptureException` mentioning a HRESULT, the most likely cause is a stale OS / GPU driver state — try once more before assuming a real regression. If it consistently fails on `Assert.Fail("No frame received before timeout.")`, a real regression has been introduced and the manager is not delivering a working device to WGC; revisit Task 4 (the framepool create call).

- [ ] **Step 2: Run the full integration test suite to catch any other regressions**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj`
Expected: all tests that previously passed still pass. Tests that require NVENC may skip (per the existing `NvidiaDriverFactAttribute` gate); that is normal.

- [ ] **Step 3: Run the unit test suite to confirm coverage gate still passes**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`
Expected: all tests pass and the 100%/100% coverage threshold is satisfied. The new code is `#if WINDOWS`-guarded and the unit test project targets `net8.0`, so it is excluded from coverage measurement entirely; no threshold concerns expected.

- [ ] **Step 4: No commit needed for verification — proceed to Task 8.**

---

## Task 8: Manual smoke check (CLI end-to-end)

The spec defers manual hardware smoke to M4 / M5, but a quick "the CLI still streams frames" sanity check at the end of M1 catches anything the integration test surface misses (e.g., interaction with the v2 worker process pipe).

- [ ] **Step 1: Build the CLI on the Windows TFM**

Run: `dotnet build src/WindowStream.Cli/WindowStream.Cli.csproj -f net8.0-windows10.0.19041.0`
Expected: success.

- [ ] **Step 2: Pick a window with active content and start the server**

Run (in a Powershell window, with an HWND from `windowstream list` for an actively-updating window — Unity, Terminal with a spinner, video player):
```powershell
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- list
# pick an HWND — copy the value
dotnet run --project src/WindowStream.Cli -f net8.0-windows10.0.19041.0 -- serve --hwnd <handle>
```
Expected: server prints the `windowstream: serving on TCP <port>, UDP <port>` banner. No exceptions on startup.

- [ ] **Step 3: Confirm a viewer can connect and render**

If a Quest 3 / GXR / phone with the viewer is available: launch DemoActivity via the adb intent pattern in `CLAUDE.md`, observe the window streaming. If no viewer hardware is conveniently available, this step is optional — the integration smoke test (Task 7) already proved frames flow through `IWindowCapture` end-to-end with the new manager. Note in the commit message of the next step which option you took.

- [ ] **Step 4: Stop the server (Ctrl-C). No commit — verification only.**

---

## Task 9: Wrap-up — commit nothing additional, summarise milestone

- [ ] **Step 1: Confirm working tree is clean**

Run: `git status`
Expected: nothing to commit, working tree clean.

- [ ] **Step 2: Confirm the M1 commits are on the branch**

Run: `git log --oneline -5`
Expected: see commits from Tasks 1, 2, 3, 5, 6 (Task 4 has no separate commit; combined with Task 5).

- [ ] **Step 3: M1 done.** Hand off to user for review and decision to proceed to M2.

---

## Self-review notes

- **Spec coverage:** M1's three spec bullets — "Add `Direct3D11DeviceManager` with unit tests" (mapped to integration tests in Task 3 since the device-creation path requires real D3D11 hardware, consistent with how `FFmpegNvencEncoder` and `WgcCaptureSource` are covered); "Wire `WgcCapture` to consume it" (Task 4); "No behavioural change" (verified by Task 7's existing smoke test passing unchanged).
- **Spec deviation: composition root left alone.** The spec also mentioned "Update the SessionHost composition root." The actual composition roots in v2 are `WorkerCommandHandler` (the per-worker capture+encode site), `CoordinatorLauncher` (enumeration only), `MauiProgram`, and `CliServices`. M1 keeps per-capture lifetime by having `WgcCaptureSource.Start()` construct the manager — this defers the hoisting decision to M4 where the encoder needs to share, and minimises the M1 diff. The spec's intent ("set up the device-sharing primitive") is preserved; the implementation locus is just one layer lower.
- **Coverage gate:** Verified that `tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj` targets `net8.0` (not `net8.0-windows10.0.19041.0`), so all `#if WINDOWS`-guarded code added in M1 is automatically excluded from coverage measurement. The integration test project provides hardware coverage in line with existing pattern.
- **Bisect-friendly history:** Task 4 deliberately leaves the build broken until Task 5 lands, then both commit together. The skill's "frequent commits" preference is honored everywhere else (Tasks 1, 2, 3, 5, 6 each commit independently).
- **No placeholders.** Every step has either runnable code, a runnable command, or a concrete decision (e.g., Task 8 step 3's optional viewer check).
- **Type consistency.** `Direct3D11DeviceManager` exposes `WinRtDevice` (property name), `NativeDevicePointer`, `NativeContextPointer` consistently across Tasks 1, 2, 3, 4. `nint` used uniformly for native pointers (matches `FFmpegNvencEncoder` style).
