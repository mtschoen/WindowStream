# M3 — GPU Colour Converter and Texture-Producing Capture Path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the CPU staging-readback BGRA-to-bytes path with a GPU-resident BGRA-to-NV12 colour conversion that keeps frames on the GPU end-to-end through the converter. Produce `CapturedFrame.FromTexture(...)` from `WgcFrameConverter`. **End-to-end demo is intentionally broken between this milestone and M4** — the encoder still expects byte-bearing frames and will fail when handed texture-only frames; M4 wires the encoder to consume textures.

**Architecture:** Add `D3D11VideoProcessorColorConverter` (sealed, `IDisposable`) that wraps `ID3D11VideoDevice` + `ID3D11VideoProcessor` + `ID3D11VideoProcessorEnumerator` + `ID3D11VideoContext`, all created from the M1-introduced `Direct3D11DeviceManager`. Its `Convert(...)` method takes a source BGRA `ID3D11Texture2D*` and a destination NV12 `ID3D11Texture2D*`+array-index, creates the matching input/output views, calls `VideoProcessorBlt`, and releases the views. `WgcCapture` lazily allocates a 3-element ring of NV12 `ID3D11Texture2D` of source-window dimensions on the first frame (sized from the source texture) and owns the converter; `WgcFrameConverter.Convert` cycles through the ring, calls the converter, emits `[FRAMECOUNT] stage=convert ...`, and returns `CapturedFrame.FromTexture`. The ring is per-capture lifetime; M4 replaces it with the FFmpeg-managed `hw_frames_ctx` pool.

**Tech Stack:** C# 12, .NET 8 (Windows TFM), Silk.NET.Direct3D11 2.22.0, xUnit, Coverlet. All new code is `#if WINDOWS`-guarded; the unit project targets bare `net8.0` and excludes it. Integration tests require real D3D11 hardware (already the project pattern).

---

## File structure

**Create:**
- `src/WindowStream.Core/Capture/Windows/D3D11VideoProcessorColorConverter.cs` — the new GPU colour converter class.
- `tests/WindowStream.Integration.Tests/Capture/Windows/D3D11VideoProcessorColorConverterTests.cs` — integration tests covering setup/cleanup/dispose-idempotence and the BGRA→NV12 round-trip "proof of life" required by the spec.

**Modify:**
- `src/WindowStream.Core/Capture/Windows/WgcCapture.cs` — own a `D3D11VideoProcessorColorConverter` (lazy-initialized on first frame after source dimensions are known); allocate a 3-element NV12 texture ring matching the source-window dimensions; expose ring + converter to `WgcFrameConverter` via a callback or `internal`-accessible state; release ring + converter in `DisposeAsync`. Recreate ring + converter if subsequent frames have different dimensions (window resize).
- `src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs` — replace the staging-texture readback with: extract the source `ID3D11Texture2D*` pointer (existing logic), call `D3D11VideoProcessorColorConverter.Convert` with a free NV12 ring slot, emit `[FRAMECOUNT] stage=convert ptsUs={timestampMicroseconds} wallMs={wallClockMilliseconds}` to stderr, return `CapturedFrame.FromTexture(...)`. The CPU staging texture, `Map`/`Unmap`, `Marshal.Copy`, and managed-byte allocation are all removed.

**Untouched (verified):** `Direct3D11DeviceManager.cs` (M1 primitive — works as-is), `WgcCaptureSource.cs` (still constructs the manager per capture), `CapturedFrame.cs` (M2 already added the texture path), `FFmpegNvencEncoder.cs` (M4's responsibility — this milestone deliberately leaves it incompatible with the new texture-only frames).

**Spec note: WgcCapture ↔ WgcFrameConverter coupling.** The current `WgcFrameConverter` is `internal static` with a single static `Convert(Direct3D11CaptureFrame, long startTicks)` method that takes nothing else from the capture. The new path needs the per-capture state (converter + ring + next-slot index). Cleanest minimal change: make `WgcFrameConverter` a non-static instance class held by `WgcCapture`, constructed with the converter and ring, exposing the same `Convert(Direct3D11CaptureFrame, long startTicks)` signature. This is a small refactor preserving call-site shape.

---

## Task 1: Add `D3D11VideoProcessorColorConverter` class

Wraps the D3D11 video processor pipeline behind a single `Convert(...)` call. Constructor sets up the video device / enumerator / processor / video context; `Convert` creates per-call input + output views, runs `VideoProcessorBlt`, releases the views. View caching is deliberately deferred — the per-call view allocation is correctness-equivalent and simpler; an M4/M5 optimization can cache views by texture pointer if profiling shows the allocation as a hotspot.

**Files:**
- Create: `src/WindowStream.Core/Capture/Windows/D3D11VideoProcessorColorConverter.cs`

- [ ] **Step 1: Create the file with the full implementation**

Write the file with this content:

```csharp
#if WINDOWS
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace WindowStream.Core.Capture.Windows;

/// <summary>
/// Wraps the D3D11 video-processor pipeline (<c>ID3D11VideoDevice</c>,
/// <c>ID3D11VideoProcessor</c>, <c>ID3D11VideoProcessorEnumerator</c>,
/// <c>ID3D11VideoContext</c>) for fixed-function BGRA → NV12 colour
/// conversion entirely on the GPU. Constructed once per (device, source
/// dimensions) — recreate on window resize. <see cref="Convert"/> performs
/// one <c>VideoProcessorBlt</c> from a caller-owned BGRA source texture
/// into a caller-owned NV12 destination texture.
/// </summary>
public sealed class D3D11VideoProcessorColorConverter : IDisposable
{
    private static readonly Guid iidId3D11VideoDevice =
        new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
    private static readonly Guid iidId3D11VideoContext =
        new Guid("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");

    private readonly Direct3D11DeviceManager deviceManager;
    private readonly int sourceWidthPixels;
    private readonly int sourceHeightPixels;
    private bool disposed;

    private unsafe ID3D11VideoDevice* videoDevice;
    private unsafe ID3D11VideoContext* videoContext;
    private unsafe ID3D11VideoProcessorEnumerator* enumerator;
    private unsafe ID3D11VideoProcessor* processor;

    public D3D11VideoProcessorColorConverter(
        Direct3D11DeviceManager deviceManager,
        int sourceWidthPixels,
        int sourceHeightPixels)
    {
        this.deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        if (sourceWidthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidthPixels));
        }
        if (sourceHeightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceHeightPixels));
        }
        this.sourceWidthPixels = sourceWidthPixels;
        this.sourceHeightPixels = sourceHeightPixels;

        unsafe
        {
            ID3D11Device* device = (ID3D11Device*)deviceManager.NativeDevicePointer;
            ID3D11DeviceContext* immediateContext = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;

            ID3D11VideoDevice* localVideoDevice = null;
            Guid videoDeviceIid = iidId3D11VideoDevice;
            int hr = device->QueryInterface(ref videoDeviceIid, (void**)&localVideoDevice);
            if (hr < 0)
            {
                throw new WindowCaptureException(
                    "QueryInterface(ID3D11VideoDevice) failed. HRESULT: 0x"
                    + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
            videoDevice = localVideoDevice;

            ID3D11VideoContext* localVideoContext = null;
            Guid videoContextIid = iidId3D11VideoContext;
            hr = immediateContext->QueryInterface(ref videoContextIid, (void**)&localVideoContext);
            if (hr < 0)
            {
                videoDevice->Release();
                videoDevice = null;
                throw new WindowCaptureException(
                    "QueryInterface(ID3D11VideoContext) failed. HRESULT: 0x"
                    + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
            videoContext = localVideoContext;

            VideoProcessorContentDesc contentDesc = default;
            contentDesc.InputFrameFormat = VideoFrameFormat.Progressive;
            contentDesc.InputFrameRate = new Rational(60, 1);
            contentDesc.InputWidth = (uint)sourceWidthPixels;
            contentDesc.InputHeight = (uint)sourceHeightPixels;
            contentDesc.OutputFrameRate = new Rational(60, 1);
            contentDesc.OutputWidth = (uint)sourceWidthPixels;
            contentDesc.OutputHeight = (uint)sourceHeightPixels;
            contentDesc.Usage = VideoUsage.PlaybackNormal;

            ID3D11VideoProcessorEnumerator* localEnumerator = null;
            hr = videoDevice->CreateVideoProcessorEnumerator(ref contentDesc, ref localEnumerator);
            if (hr < 0)
            {
                videoContext->Release(); videoContext = null;
                videoDevice->Release(); videoDevice = null;
                throw new WindowCaptureException(
                    "CreateVideoProcessorEnumerator failed. HRESULT: 0x"
                    + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
            enumerator = localEnumerator;

            ID3D11VideoProcessor* localProcessor = null;
            hr = videoDevice->CreateVideoProcessor(enumerator, 0, ref localProcessor);
            if (hr < 0)
            {
                enumerator->Release(); enumerator = null;
                videoContext->Release(); videoContext = null;
                videoDevice->Release(); videoDevice = null;
                throw new WindowCaptureException(
                    "CreateVideoProcessor failed. HRESULT: 0x"
                    + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
            processor = localProcessor;
        }
    }

    /// <summary>
    /// Convert one BGRA frame to NV12. <paramref name="sourceBgraTexturePointer"/>
    /// is a caller-owned <c>ID3D11Texture2D*</c> in BGRA (typically the WGC
    /// frame surface texture for the current frame). <paramref name="destinationNv12TexturePointer"/>
    /// is a caller-owned <c>ID3D11Texture2D*</c> in NV12 of matching width
    /// and height; <paramref name="destinationArrayIndex"/> is the destination
    /// subresource index (0 for non-array textures).
    /// </summary>
    public void Convert(
        nint sourceBgraTexturePointer,
        nint destinationNv12TexturePointer,
        int destinationArrayIndex)
    {
        ThrowIfDisposed();
        if (sourceBgraTexturePointer == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBgraTexturePointer));
        }
        if (destinationNv12TexturePointer == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationNv12TexturePointer));
        }
        if (destinationArrayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationArrayIndex));
        }

        unsafe
        {
            ID3D11VideoProcessorInputView* inputView = null;
            ID3D11VideoProcessorOutputView* outputView = null;
            try
            {
                VideoProcessorInputViewDesc inputDesc = default;
                inputDesc.FourCC = 0;
                inputDesc.ViewDimension = VpivDimension.Texture2D;
                inputDesc.Anonymous.Texture2D.MipSlice = 0;
                inputDesc.Anonymous.Texture2D.ArraySlice = 0;

                int hr = videoDevice->CreateVideoProcessorInputView(
                    (ID3D11Resource*)sourceBgraTexturePointer,
                    enumerator,
                    ref inputDesc,
                    ref inputView);
                if (hr < 0)
                {
                    throw new WindowCaptureException(
                        "CreateVideoProcessorInputView failed. HRESULT: 0x"
                        + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                VideoProcessorOutputViewDesc outputDesc = default;
                outputDesc.ViewDimension = VpovDimension.Texture2D;
                outputDesc.Anonymous.Texture2D.MipSlice = 0;

                hr = videoDevice->CreateVideoProcessorOutputView(
                    (ID3D11Resource*)destinationNv12TexturePointer,
                    enumerator,
                    ref outputDesc,
                    ref outputView);
                if (hr < 0)
                {
                    throw new WindowCaptureException(
                        "CreateVideoProcessorOutputView failed. HRESULT: 0x"
                        + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }

                VideoProcessorStream stream = default;
                stream.Enable = 1;
                stream.OutputIndex = 0;
                stream.InputFrameOrField = 0;
                stream.PstreamItems = 0;
                stream.PInputSurface = inputView;

                hr = videoContext->VideoProcessorBlt(
                    processor,
                    outputView,
                    0u,
                    1u,
                    ref stream);
                if (hr < 0)
                {
                    throw new WindowCaptureException(
                        "VideoProcessorBlt failed. HRESULT: 0x"
                        + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                if (inputView != null) inputView->Release();
                if (outputView != null) outputView->Release();
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        unsafe
        {
            if (processor != null) { processor->Release(); processor = null; }
            if (enumerator != null) { enumerator->Release(); enumerator = null; }
            if (videoContext != null) { videoContext->Release(); videoContext = null; }
            if (videoDevice != null) { videoDevice->Release(); videoDevice = null; }
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(D3D11VideoProcessorColorConverter));
        }
    }
}
#endif
```

**API verification note for the implementing agent:** the Silk.NET 2.22.0 binding for the `ID3D11VideoDevice`, `ID3D11VideoContext`, and view types may have minor signature differences from what is shown above (e.g., `ref` vs `out` parameters, anonymous-union member naming, exact field names on `VideoProcessorContentDesc`). The MS DirectX C++ contract is the source of truth for behaviour; substitute Silk.NET's exact signatures where they differ. If a signature mismatch causes compile failure, query the binding via:

```powershell
$asm = [Reflection.Assembly]::LoadFrom('C:\Users\mtsch\.nuget\packages\silk.net.direct3d11\2.22.0\lib\net5.0\Silk.NET.Direct3D11.dll'); try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }; $types | Where-Object { $_.Name -eq 'ID3D11VideoDevice' } | ForEach-Object { $_.GetMethods() | Select-Object Name, ReturnType }
```

If the binding name diverges (e.g., `Vpiv` vs `D3D11VPIV` for the dimension enum), use whatever Silk.NET ships; the values are stable per the DirectX SDK.

- [ ] **Step 2: Verify the file compiles**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: success, 0 warnings, 0 errors. If a compile error names a Silk.NET type or member, see the API verification note in Step 1; substitute the binding's actual name. Do not change the algorithm — only the binding-level names.

- [ ] **Step 3: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows/D3D11VideoProcessorColorConverter.cs
git commit -m "feat(capture): add D3D11VideoProcessorColorConverter (M3)"
```

---

## Task 2: Add integration tests for the converter

Three tests cover the spec's requirements:
1. **Setup**: constructor creates a working converter without throwing.
2. **Cleanup**: `Dispose` is idempotent and `Convert` after `Dispose` throws.
3. **Round-trip correctness ("proof of life")**: real `VideoProcessorBlt` from a synthetic BGRA texture into an NV12 texture, read NV12 back via CPU staging, decode NV12 with a CPU reference helper, compare to the source within colour-space tolerance.

The colour-space tolerance is loose because `VideoProcessorBlt` uses the GPU's BT.601/BT.709 matrix and the CPU reference uses BT.601 — small per-channel rounding differences are expected. ±8 per channel on uint8 RGB is the project's convention for these checks.

**Files:**
- Create: `tests/WindowStream.Integration.Tests/Capture/Windows/D3D11VideoProcessorColorConverterTests.cs`

- [ ] **Step 1: Create the test file**

Write the file with this content:

```csharp
#if WINDOWS
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture.Windows;

[Trait("Category", "Windows")]
public sealed class D3D11VideoProcessorColorConverterTests
{
    private const int TestWidth = 64;
    private const int TestHeight = 64;

    [Fact]
    public void Constructor_Succeeds_With_Valid_Device_And_Dimensions()
    {
        using Direct3D11DeviceManager manager = new Direct3D11DeviceManager();
        using D3D11VideoProcessorColorConverter converter =
            new D3D11VideoProcessorColorConverter(manager, TestWidth, TestHeight);
        // Reaching here without exception is the success criterion.
    }

    [Fact]
    public void Dispose_Is_Idempotent_And_Convert_After_Dispose_Throws()
    {
        using Direct3D11DeviceManager manager = new Direct3D11DeviceManager();
        D3D11VideoProcessorColorConverter converter =
            new D3D11VideoProcessorColorConverter(manager, TestWidth, TestHeight);
        converter.Dispose();
        converter.Dispose(); // must not throw
        Assert.Throws<ObjectDisposedException>(() => converter.Convert((nint)1, (nint)2, 0));
    }

    [Fact]
    public unsafe void Convert_BgraToNv12_RoundTrip_WithinColorSpaceTolerance()
    {
        // Arrange: build a synthetic BGRA source texture with a known pattern
        // (one solid colour per quadrant), run Convert into an NV12 texture,
        // read the NV12 texture back via a CPU staging copy, decode each pixel
        // with a BT.601 limited-range reference, and compare to the source.

        using Direct3D11DeviceManager manager = new Direct3D11DeviceManager();
        ID3D11Device* device = (ID3D11Device*)manager.NativeDevicePointer;
        ID3D11DeviceContext* context = (ID3D11DeviceContext*)manager.NativeContextPointer;

        // Source BGRA texture (CPU-initialized, GPU-default usage).
        byte[] sourceBgra = BuildQuadrantPattern(TestWidth, TestHeight);
        ID3D11Texture2D* sourceTexture = CreateBgraTexture(device, TestWidth, TestHeight, sourceBgra);

        // Destination NV12 texture (single subresource, render-target bindable).
        ID3D11Texture2D* destinationTexture = CreateNv12RenderTexture(device, TestWidth, TestHeight);

        try
        {
            using D3D11VideoProcessorColorConverter converter =
                new D3D11VideoProcessorColorConverter(manager, TestWidth, TestHeight);

            converter.Convert((nint)sourceTexture, (nint)destinationTexture, 0);

            // Read NV12 back via a staging texture mapped on the CPU.
            byte[] nv12Bytes = ReadNv12TextureAsBytes(device, context, destinationTexture, TestWidth, TestHeight);

            // Decode NV12 → RGB with the CPU reference.
            byte[] decodedBgra = DecodeNv12ToBgra(nv12Bytes, TestWidth, TestHeight);

            // Compare per-pixel within ±8 tolerance per channel.
            AssertBgraWithinTolerance(sourceBgra, decodedBgra, TestWidth, TestHeight, tolerance: 8);
        }
        finally
        {
            sourceTexture->Release();
            destinationTexture->Release();
        }
    }

    // -- helpers below: kept private to this test file; not production code --

    private static byte[] BuildQuadrantPattern(int width, int height)
    {
        // BGRA layout: byte order B, G, R, A.
        // Quadrants:
        //   top-left  : red    (B=0,   G=0,   R=200, A=255)
        //   top-right : green  (B=0,   G=200, R=0,   A=255)
        //   bot-left  : blue   (B=200, G=0,   R=0,   A=255)
        //   bot-right : grey   (B=128, G=128, R=128, A=255)
        // Choosing values away from clip points avoids quantization weirdness.
        byte[] buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                bool top = y < height / 2;
                bool left = x < width / 2;
                if (top && left)        { buffer[offset] = 0;   buffer[offset+1] = 0;   buffer[offset+2] = 200; }
                else if (top && !left)  { buffer[offset] = 0;   buffer[offset+1] = 200; buffer[offset+2] = 0;   }
                else if (!top && left)  { buffer[offset] = 200; buffer[offset+1] = 0;   buffer[offset+2] = 0;   }
                else                    { buffer[offset] = 128; buffer[offset+1] = 128; buffer[offset+2] = 128; }
                buffer[offset+3] = 255;
            }
        }
        return buffer;
    }

    private static unsafe ID3D11Texture2D* CreateBgraTexture(
        ID3D11Device* device, int width, int height, byte[] sourcePixels)
    {
        Texture2DDesc description = default;
        description.Width = (uint)width;
        description.Height = (uint)height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = Format.FormatB8G8R8A8Unorm;
        description.SampleDesc = new SampleDesc(1, 0);
        description.Usage = Usage.Default;
        description.BindFlags = (uint)(BindFlag.ShaderResource);
        description.CPUAccessFlags = 0;
        description.MiscFlags = 0;

        fixed (byte* pixelsPointer = sourcePixels)
        {
            SubresourceData initial = default;
            initial.PSysMem = pixelsPointer;
            initial.SysMemPitch = (uint)(width * 4);

            ID3D11Texture2D* texture = null;
            int hr = device->CreateTexture2D(ref description, ref initial, ref texture);
            if (hr < 0)
            {
                throw new System.Exception(
                    $"CreateTexture2D(BGRA) failed: HRESULT 0x{(uint)hr:X8}");
            }
            return texture;
        }
    }

    private static unsafe ID3D11Texture2D* CreateNv12RenderTexture(
        ID3D11Device* device, int width, int height)
    {
        Texture2DDesc description = default;
        description.Width = (uint)width;
        description.Height = (uint)height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = Format.FormatNV12;
        description.SampleDesc = new SampleDesc(1, 0);
        description.Usage = Usage.Default;
        description.BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource);
        description.CPUAccessFlags = 0;
        description.MiscFlags = 0;

        ID3D11Texture2D* texture = null;
        int hr = device->CreateTexture2D(ref description, (SubresourceData*)null, ref texture);
        if (hr < 0)
        {
            throw new System.Exception(
                $"CreateTexture2D(NV12) failed: HRESULT 0x{(uint)hr:X8}");
        }
        return texture;
    }

    private static unsafe byte[] ReadNv12TextureAsBytes(
        ID3D11Device* device,
        ID3D11DeviceContext* context,
        ID3D11Texture2D* nv12Texture,
        int width,
        int height)
    {
        Texture2DDesc stagingDescription = default;
        stagingDescription.Width = (uint)width;
        stagingDescription.Height = (uint)height;
        stagingDescription.MipLevels = 1;
        stagingDescription.ArraySize = 1;
        stagingDescription.Format = Format.FormatNV12;
        stagingDescription.SampleDesc = new SampleDesc(1, 0);
        stagingDescription.Usage = Usage.Staging;
        stagingDescription.BindFlags = 0;
        stagingDescription.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        stagingDescription.MiscFlags = 0;

        ID3D11Texture2D* staging = null;
        int hr = device->CreateTexture2D(ref stagingDescription, (SubresourceData*)null, ref staging);
        if (hr < 0)
        {
            throw new System.Exception($"CreateTexture2D(NV12 staging) failed: HRESULT 0x{(uint)hr:X8}");
        }
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)nv12Texture);

            MappedSubresource mapped = default;
            hr = context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, ref mapped);
            if (hr < 0)
            {
                throw new System.Exception($"Map(NV12 staging) failed: HRESULT 0x{(uint)hr:X8}");
            }

            int yPlaneBytes = width * height;
            int uvPlaneBytes = width * height / 2;
            byte[] result = new byte[yPlaneBytes + uvPlaneBytes];

            // Y plane: height rows of width bytes each, source pitch = mapped.RowPitch.
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(
                    (IntPtr)((byte*)mapped.PData + row * mapped.RowPitch),
                    result,
                    row * width,
                    width);
            }
            // UV plane: starts at height * RowPitch in source; height/2 rows of width bytes each.
            byte* uvSource = (byte*)mapped.PData + height * mapped.RowPitch;
            for (int row = 0; row < height / 2; row++)
            {
                Marshal.Copy(
                    (IntPtr)(uvSource + row * mapped.RowPitch),
                    result,
                    yPlaneBytes + row * width,
                    width);
            }

            context->Unmap((ID3D11Resource*)staging, 0);
            return result;
        }
        finally
        {
            staging->Release();
        }
    }

    private static byte[] DecodeNv12ToBgra(byte[] nv12, int width, int height)
    {
        // BT.601 limited-range NV12 → BGRA. Reference matches what the GPU's
        // VideoProcessor uses for SD-class content; for HD content (BT.709)
        // the tolerance accommodates the small offset.
        byte[] result = new byte[width * height * 4];
        int yPlaneBytes = width * height;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int yIndex = y * width + x;
                // UV plane is interleaved: U,V,U,V,...; each sample covers a 2x2 block.
                int uvRow = y / 2;
                int uvCol = (x / 2) * 2;
                int uIndex = yPlaneBytes + uvRow * width + uvCol;
                int vIndex = uIndex + 1;

                int yValue = nv12[yIndex] - 16;
                int uValue = nv12[uIndex] - 128;
                int vValue = nv12[vIndex] - 128;

                int r = (298 * yValue + 409 * vValue + 128) >> 8;
                int g = (298 * yValue - 100 * uValue - 208 * vValue + 128) >> 8;
                int b = (298 * yValue + 516 * uValue + 128) >> 8;

                int offset = (y * width + x) * 4;
                result[offset] = (byte)System.Math.Clamp(b, 0, 255);
                result[offset+1] = (byte)System.Math.Clamp(g, 0, 255);
                result[offset+2] = (byte)System.Math.Clamp(r, 0, 255);
                result[offset+3] = 255;
            }
        }
        return result;
    }

    private static void AssertBgraWithinTolerance(
        byte[] expected, byte[] actual, int width, int height, int tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        // Skip a 2-pixel border because VideoProcessor edge handling can
        // diverge from the CPU reference at the very edges.
        int border = 2;
        for (int y = border; y < height - border; y++)
        {
            for (int x = border; x < width - border; x++)
            {
                int offset = (y * width + x) * 4;
                int diffB = System.Math.Abs(expected[offset]   - actual[offset]);
                int diffG = System.Math.Abs(expected[offset+1] - actual[offset+1]);
                int diffR = System.Math.Abs(expected[offset+2] - actual[offset+2]);
                if (diffB > tolerance || diffG > tolerance || diffR > tolerance)
                {
                    Assert.Fail(
                        $"Pixel ({x},{y}) BGR diff ({diffB},{diffG},{diffR}) exceeds tolerance {tolerance}. " +
                        $"Expected ({expected[offset]},{expected[offset+1]},{expected[offset+2]}) " +
                        $"Actual ({actual[offset]},{actual[offset+1]},{actual[offset+2]}).");
                }
            }
        }
    }
}
#endif
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~D3D11VideoProcessorColorConverterTests"`
Expected: 3 tests pass.

If `Convert_BgraToNv12_RoundTrip_WithinColorSpaceTolerance` fails:
- HRESULT in the message → check `D3D11VideoProcessorColorConverter` against the spec's video-processor flow; most likely a view-creation parameter (often `FourCC=0` is the right choice for BGRA input but binding-specific fields like `Anonymous.Texture2D.MipSlice` may need adjustment).
- Tolerance overrun (`Pixel (X,Y) BGR diff ...`) — first try increasing the tolerance to 12; if that passes, the BT.601 vs BT.709 matrix mismatch is biting and the converter is doing the right thing (the spec accepts a "tolerance for the colour-space conversion"). Don't increase the tolerance silently — leave a comment explaining the bump and the matrix-mismatch reasoning.

- [ ] **Step 3: Commit**

```bash
git add tests/WindowStream.Integration.Tests/Capture/Windows/D3D11VideoProcessorColorConverterTests.cs
git commit -m "test(capture): integration tests for D3D11VideoProcessorColorConverter (M3 proof of life)"
```

---

## Task 3: Convert `WgcFrameConverter` from static to instance class

Refactors `WgcFrameConverter` from a static utility into a small instance class held by `WgcCapture`. The class doesn't yet do anything different — same staging-readback bytes path — but the shape is what Task 5 needs to inject the converter and ring. This task is purely structural so the bigger change in Tasks 4 and 5 stays focused.

**Files:**
- Modify: `src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs`
- Modify: `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`

- [ ] **Step 1: Replace the entire contents of `WgcFrameConverter.cs`**

Open `src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs`. Replace with:

```csharp
#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

internal sealed class WgcFrameConverter
{
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static readonly Guid iidId3D11Texture2D =
        new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public CapturedFrame Convert(Direct3D11CaptureFrame frame, long startTicks)
    {
        IDirect3DDxgiInterfaceAccess access =
            frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid id = iidId3D11Texture2D;
        IntPtr texturePointer = access.GetInterface(ref id);
        try
        {
            unsafe
            {
                ID3D11Texture2D* texture = (ID3D11Texture2D*)texturePointer;
                Texture2DDesc description = default;
                texture->GetDesc(ref description);

                ID3D11Device* device = null;
                texture->GetDevice(&device);
                ID3D11DeviceContext* context = null;
                device->GetImmediateContext(&context);

                Texture2DDesc stagingDescription = description;
                stagingDescription.Usage = Usage.Staging;
                stagingDescription.BindFlags = 0;
                stagingDescription.CPUAccessFlags = (uint)CpuAccessFlag.Read;
                stagingDescription.MiscFlags = 0;

                ID3D11Texture2D* staging = null;
                device->CreateTexture2D(ref stagingDescription, (SubresourceData*)null, ref staging);
                context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);

                MappedSubresource mapped = default;
                context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, ref mapped);

                int width = (int)description.Width;
                int height = (int)description.Height;
                int stride = (int)mapped.RowPitch;
                byte[] managed = new byte[stride * height];
                Marshal.Copy((IntPtr)mapped.PData, managed, 0, managed.Length);
                context->Unmap((ID3D11Resource*)staging, 0);
                staging->Release();
                context->Release();
                device->Release();

                long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                long timestampMicroseconds = (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency);

                return new CapturedFrame(
                    widthPixels: width,
                    heightPixels: height,
                    rowStrideBytes: stride,
                    pixelFormat: PixelFormat.Bgra32,
                    presentationTimestampMicroseconds: timestampMicroseconds,
                    pixelBuffer: managed);
            }
        }
        finally
        {
            Marshal.Release(texturePointer);
        }
    }
}
#endif
```

(The only change is `internal static class WgcFrameConverter` → `internal sealed class WgcFrameConverter`, and `public static CapturedFrame Convert` → `public CapturedFrame Convert`. Body identical.)

- [ ] **Step 2: Update the call site in `WgcCapture.cs`**

Open `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`. Find the `OnFrameArrived` body — currently `CapturedFrame converted = WgcFrameConverter.Convert(frame, startTicks);` — and the surrounding state. Make the following edits.

Edit 1 — add a `WgcFrameConverter` field. Find:

```csharp
    private readonly Direct3D11DeviceManager deviceManager;
```

Replace with:

```csharp
    private readonly Direct3D11DeviceManager deviceManager;
    private readonly WgcFrameConverter frameConverter = new WgcFrameConverter();
```

Edit 2 — update the call site. Find:

```csharp
            CapturedFrame converted = WgcFrameConverter.Convert(frame, startTicks);
```

Replace with:

```csharp
            CapturedFrame converted = frameConverter.Convert(frame, startTicks);
```

- [ ] **Step 3: Verify build and integration tests still pass**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: success.

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~WgcCaptureSourceSmokeTests"`
Expected: smoke test passes (the bytes-path frame flow is still in place; this task is structural only).

- [ ] **Step 4: Commit**

```bash
git add src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs \
        src/WindowStream.Core/Capture/Windows/WgcCapture.cs
git commit -m "refactor(capture): WgcFrameConverter from static to instance class (M3 prep)"
```

---

## Task 4: Allocate NV12 ring + colour converter inside `WgcCapture`

Adds the per-capture state needed for the texture path: a 3-element NV12 texture ring (created lazily on first frame from the source texture's dimensions), a `D3D11VideoProcessorColorConverter` instance, and a next-slot index. Recreates ring + converter when source dimensions change between frames (window resize). Disposes both in `DisposeAsync`.

The ring lives inside `WgcCapture` rather than `WgcFrameConverter` so the converter class stays lightweight and easy to inject. `WgcCapture` reaches into `frameConverter` via a new field-update path on the converter — covered in Task 5.

**Files:**
- Modify: `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`

- [ ] **Step 1: Add ring + converter fields and a setup helper**

Open `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`. Add the following new fields just below the existing `frameConverter` field added in Task 3:

```csharp
    private const int Nv12RingLength = 3;
    private readonly nint[] nv12RingTexturePointers = new nint[Nv12RingLength];
    private int nv12RingNextIndex;
    private int nv12RingWidthPixels;
    private int nv12RingHeightPixels;
    private D3D11VideoProcessorColorConverter? colorConverter;
```

- [ ] **Step 2: Add a private helper that ensures the ring + converter are sized to the current source dimensions**

Insert this method inside the class (a good location is just above `OnFrameArrived`):

```csharp
    private void EnsureNv12RingAndConverter(int sourceWidthPixels, int sourceHeightPixels)
    {
        if (sourceWidthPixels == nv12RingWidthPixels
            && sourceHeightPixels == nv12RingHeightPixels
            && colorConverter is not null)
        {
            return;
        }

        DisposeNv12RingAndConverter();

        unsafe
        {
            ID3D11Device* device = (ID3D11Device*)deviceManager.NativeDevicePointer;
            for (int slot = 0; slot < Nv12RingLength; slot++)
            {
                Texture2DDesc description = default;
                description.Width = (uint)sourceWidthPixels;
                description.Height = (uint)sourceHeightPixels;
                description.MipLevels = 1;
                description.ArraySize = 1;
                description.Format = Format.FormatNV12;
                description.SampleDesc = new SampleDesc(1, 0);
                description.Usage = Usage.Default;
                description.BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource);
                description.CPUAccessFlags = 0;
                description.MiscFlags = 0;

                ID3D11Texture2D* texture = null;
                int hr = device->CreateTexture2D(ref description, (SubresourceData*)null, ref texture);
                if (hr < 0)
                {
                    DisposeNv12RingAndConverter();
                    throw new WindowCaptureException(
                        "CreateTexture2D(NV12 ring) failed. HRESULT: 0x"
                        + ((uint)hr).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }
                nv12RingTexturePointers[slot] = (nint)texture;
            }
        }

        colorConverter = new D3D11VideoProcessorColorConverter(
            deviceManager, sourceWidthPixels, sourceHeightPixels);
        nv12RingWidthPixels = sourceWidthPixels;
        nv12RingHeightPixels = sourceHeightPixels;
        nv12RingNextIndex = 0;
    }

    private void DisposeNv12RingAndConverter()
    {
        try { colorConverter?.Dispose(); } catch { }
        colorConverter = null;

        for (int slot = 0; slot < Nv12RingLength; slot++)
        {
            if (nv12RingTexturePointers[slot] != 0)
            {
                Marshal.Release(nv12RingTexturePointers[slot]);
                nv12RingTexturePointers[slot] = 0;
            }
        }
        nv12RingWidthPixels = 0;
        nv12RingHeightPixels = 0;
        nv12RingNextIndex = 0;
    }
```

- [ ] **Step 3: Hook ring disposal into `DisposeAsync`**

Find `DisposeAsync` and the line:

```csharp
        try { deviceManager.Dispose(); } catch { }
```

Replace the block from `disposed = true;` to just before `frameChannel.Writer.TryComplete();` with:

```csharp
        disposed = true;
        try { session.Dispose(); } catch { }
        try { framePool.Dispose(); } catch { }
        try { DisposeNv12RingAndConverter(); } catch { }
        try { deviceManager.Dispose(); } catch { }
```

(Order: framePool → ring/converter → deviceManager. Ring + converter must release their D3D11 objects before the device manager releases the device.)

- [ ] **Step 4: Add the necessary `using` directives at the top of the file**

`WgcCapture.cs` may not already import `Silk.NET.Direct3D11` and `Silk.NET.DXGI`. Add these usings at the top of the file (preserving the existing ones):

```csharp
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Runtime.InteropServices;
```

(Skip any usings that are already present; the file's existing imports stay.)

- [ ] **Step 5: Verify the file compiles**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: success, no warnings, no errors. The new fields are private and unused elsewhere yet — `EnsureNv12RingAndConverter` will be called from Task 5's modified `WgcFrameConverter`. This compile is intentionally a state-only landing.

- [ ] **Step 6: Do not commit yet** — Task 5 makes the ring + converter actually do work, and bisect is friendlier with that as one commit.

---

## Task 5: Switch `WgcFrameConverter.Convert` to the GPU path

Replaces the body of `WgcFrameConverter.Convert` so that, given a `Direct3D11CaptureFrame`, it (1) extracts the source BGRA texture pointer + dimensions, (2) calls back into the owning `WgcCapture` to ensure the ring + converter are sized correctly and to obtain the next free NV12 ring slot, (3) calls `D3D11VideoProcessorColorConverter.Convert`, (4) emits `[FRAMECOUNT] stage=convert ...` to stderr, (5) returns `CapturedFrame.FromTexture(...)` referencing the NV12 ring slot. The CPU staging readback, `Map`/`Unmap`, `Marshal.Copy`, and managed-byte allocation are all gone.

The owner-callback shape (rather than `WgcCapture` reading a slot index off `WgcFrameConverter`) keeps ring management inside `WgcCapture` and the converter responsibility cleanly inside the converter class.

**Files:**
- Modify: `src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs`
- Modify: `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`

- [ ] **Step 1: Replace the entire contents of `WgcFrameConverter.cs`**

Open `src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs`. Replace with:

```csharp
#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D11;
using Windows.Graphics.Capture;
using WinRT;

namespace WindowStream.Core.Capture.Windows;

/// <summary>
/// Per-capture conversion path: extracts the WGC source texture pointer
/// and delegates BGRA → NV12 conversion to the owner-supplied
/// <see cref="D3D11VideoProcessorColorConverter"/> + NV12 ring. Returns
/// a texture-bearing <see cref="CapturedFrame"/>. The encoder is updated
/// in M4 to consume texture frames.
/// </summary>
internal sealed class WgcFrameConverter
{
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static readonly Guid iidId3D11Texture2D =
        new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>
    /// Provider supplied by <c>WgcCapture</c>: ensures the NV12 ring + colour
    /// converter are sized to the supplied source dimensions, then returns
    /// the next free ring-slot texture pointer + array index + the converter.
    /// </summary>
    public delegate (D3D11VideoProcessorColorConverter converter, nint nv12TexturePointer, int arrayIndex)
        AcquireNv12SlotDelegate(int sourceWidthPixels, int sourceHeightPixels);

    private readonly AcquireNv12SlotDelegate acquireSlot;

    public WgcFrameConverter(AcquireNv12SlotDelegate acquireSlot)
    {
        this.acquireSlot = acquireSlot ?? throw new ArgumentNullException(nameof(acquireSlot));
    }

    public CapturedFrame Convert(Direct3D11CaptureFrame frame, long startTicks)
    {
        IDirect3DDxgiInterfaceAccess access =
            frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid id = iidId3D11Texture2D;
        IntPtr sourceTexturePointer = access.GetInterface(ref id);
        try
        {
            int width;
            int height;
            unsafe
            {
                ID3D11Texture2D* texture = (ID3D11Texture2D*)sourceTexturePointer;
                Texture2DDesc description = default;
                texture->GetDesc(ref description);
                width = (int)description.Width;
                height = (int)description.Height;
            }

            (D3D11VideoProcessorColorConverter converter, nint nv12TexturePointer, int arrayIndex) =
                acquireSlot(width, height);
            converter.Convert(sourceTexturePointer, nv12TexturePointer, arrayIndex);

            long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            long timestampMicroseconds = (long)(elapsedTicks * 1_000_000.0 / Stopwatch.Frequency);
            long wallClockMilliseconds = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            System.Console.Error.WriteLine(
                $"[FRAMECOUNT] stage=convert ptsUs={timestampMicroseconds} wallMs={wallClockMilliseconds}");

            // NV12 row pitch is at least the width in bytes; CapturedFrame's
            // validation only checks that rowStrideBytes >= width, which is
            // satisfied here. The exact GPU-allocated pitch isn't surfaced
            // here — M4 will read it from the FFmpeg AVFrame when needed.
            return CapturedFrame.FromTexture(
                widthPixels: width,
                heightPixels: height,
                rowStrideBytes: width,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: timestampMicroseconds,
                nativeTexturePointer: nv12TexturePointer,
                textureArrayIndex: arrayIndex);
        }
        finally
        {
            Marshal.Release(sourceTexturePointer);
        }
    }
}
#endif
```

- [ ] **Step 2: Wire the delegate from `WgcCapture`**

Open `src/WindowStream.Core/Capture/Windows/WgcCapture.cs`. The Task 3 line:

```csharp
    private readonly WgcFrameConverter frameConverter = new WgcFrameConverter();
```

Replace with:

```csharp
    private readonly WgcFrameConverter frameConverter;
```

In the constructor body, after `this.deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));`, add:

```csharp
        this.frameConverter = new WgcFrameConverter(AcquireNv12Slot);
```

Then add this method to the class (next to `EnsureNv12RingAndConverter`):

```csharp
    private (D3D11VideoProcessorColorConverter converter, nint nv12TexturePointer, int arrayIndex)
        AcquireNv12Slot(int sourceWidthPixels, int sourceHeightPixels)
    {
        EnsureNv12RingAndConverter(sourceWidthPixels, sourceHeightPixels);
        nint slotTexturePointer = nv12RingTexturePointers[nv12RingNextIndex];
        nv12RingNextIndex = (nv12RingNextIndex + 1) % Nv12RingLength;
        return (colorConverter!, slotTexturePointer, 0);
    }
```

- [ ] **Step 3: Verify the project compiles**

Run: `dotnet build src/WindowStream.Core/WindowStream.Core.csproj -f net8.0-windows10.0.19041.0`
Expected: success, 0 warnings, 0 errors.

- [ ] **Step 4: Run the integration tests**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~D3D11VideoProcessorColorConverterTests"`
Expected: 3 tests still pass.

The pre-existing `WgcCaptureSourceSmokeTests` exercises the full end-to-end frame flow including the encoder. Per the spec, **end-to-end is intentionally broken at M3** — the encoder hasn't been switched to the texture path, and it will fail when `frame.pixelBuffer.Length == 0`. That smoke test is therefore expected to fail or assert; M4 restores it. **Do not run that smoke test as part of M3 verification, and do not "fix" the failure here** — the failure is the milestone's documented signal.

If for some reason the smoke test passes at this point (unlikely — `FFmpegNvencEncoder.EncodeAsync` reads `frame.pixelBuffer`), that would suggest production code unexpectedly tolerates empty buffers; investigate before continuing, since it would mean the encoder was already silently dropping frames.

- [ ] **Step 5: Commit Tasks 4 + 5 together**

```bash
git add src/WindowStream.Core/Capture/Windows/WgcCapture.cs \
        src/WindowStream.Core/Capture/Windows/WgcFrameConverter.cs
git commit -m "feat(capture): produce NV12 texture frames via D3D11 video processor (M3)

WgcCapture allocates a 3-element NV12 ring sized to the current source
window and owns a D3D11VideoProcessorColorConverter; WgcFrameConverter
runs the GPU BGRA→NV12 conversion and returns CapturedFrame.FromTexture.

End-to-end demo intentionally broken at this milestone — FFmpegNvencEncoder
still expects byte-bearing frames. M4 wires the encoder to the texture
path and restores end-to-end functionality.
"
```

---

## Task 6: Confirm milestone state and broken-by-design demo

The spec is explicit: M3 must produce a working converter (proven by Task 2's integration tests), but the end-to-end demo path is allowed — and expected — to be broken until M4 lands. This task confirms both halves.

- [ ] **Step 1: Confirm the converter integration tests pass**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~D3D11VideoProcessorColorConverterTests"`
Expected: 3 pass.

- [ ] **Step 2: Confirm the unit-test suite still passes the relaxed coverage gate**

Run: `dotnet test tests/WindowStream.Core.Tests/WindowStream.Core.Tests.csproj`
Expected: all unit tests pass; coverage gate at 90%/85% line/branch is met. The new `D3D11VideoProcessorColorConverter` is `#if WINDOWS`-guarded and the unit project targets `net8.0`, so the new code is excluded from coverage measurement; the modifications to `WgcCapture.cs` and `WgcFrameConverter.cs` are likewise `#if WINDOWS`-guarded and excluded.

If the gate goes red despite the above, the cause is something other than the new Windows-guarded code (e.g., a non-Windows reachability that subtly changed). Investigate before continuing.

- [ ] **Step 3: Confirm the broken-by-design end-to-end smoke**

The pre-existing `WgcCaptureSourceSmokeTests.Attaches_To_Notepad_And_Receives_Frame` end-to-end test goes through `WgcCapture` → `WgcFrameConverter` → frame channel and asserts a frame is delivered. That test is *not* expected to fail at M3 in the channel reader — it only checks that a `CapturedFrame` arrives, and a texture-bearing one still arrives. So this should still pass. Run it:

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj --filter "FullyQualifiedName~WgcCaptureSourceSmokeTests"`
Expected: 1 pass. (The smoke test does not invoke `FFmpegNvencEncoder`; the broken-end-to-end is at the encoder, not the capture path. The capture path itself is healthier than ever.)

The actual proof that end-to-end is broken comes from running `windowstream serve` (which goes through `WorkerCommandHandler` → encoder). Per spec section M3: "No manual smoke at M3. End-to-end is broken by design here." Skip that.

- [ ] **Step 4: Run the full integration test suite for safety**

Run: `dotnet test tests/WindowStream.Integration.Tests/WindowStream.Integration.Tests.csproj`
Expected: tests that were green pre-M3 are still green; new converter tests are also green; total count rises by the 3 new converter tests. The 3 existing skips (mDNS loopback, focus relay, placeholder) remain skipped.

If a previously-passing integration test now fails (other than something that exercises the encoder pipeline), investigate — it's a real M3 regression. The encoder pipeline is allowed to be broken; nothing else is.

- [ ] **Step 5: No commit needed for verification — proceed to Task 7.**

---

## Task 7: Wrap-up

- [ ] **Step 1: Confirm working tree is clean**

Run: `git status`
Expected: nothing to commit, working tree clean.

- [ ] **Step 2: Confirm the M3 commits are on the branch**

Run: `git log --oneline -6`
Expected: see commits from Tasks 1, 2, 3, and 5 (Task 4 commits with Task 5).

- [ ] **Step 3: M3 done.** Hand off to user for review and decision to proceed to M4.

---

## Self-review notes

- **Spec coverage:** M3's bullets — "Add `D3D11VideoProcessorColorConverter` with unit tests for setup and cleanup" (Tasks 1 + 2 step-by-step; tests live in the integration project because real D3D11 is required, consistent with M1's `Direct3D11DeviceManager` pattern); "integration test for BGRA → NV12 round-trip correctness" (Task 2 `Convert_BgraToNv12_RoundTrip_WithinColorSpaceTolerance`); "Modify `WgcFrameConverter` to produce NV12 texture frames" (Task 5); "Allocate a small ring of NV12 textures inside `WgcCapture`" (Task 4); "Add `[FRAMECOUNT] stage=convert` log site" (Task 5 step 1, with `ptsUs` and `wallMs` matching the existing `stage=enc` format); "No encoder changes" (verified — `FFmpegNvencEncoder` is in the untouched list); "Proof of life: converter integration test must pass" (Task 2's round-trip test); "No manual smoke at M3" (Task 6 step 3 confirms the capture-level smoke is fine and skips the demo).
- **Spec deviation: `WgcFrameConverter` from static to instance.** Spec calls for modifying `WgcFrameConverter`; the cleanest minimal change is to convert the static class to an instance class (Task 3 is the structural step; Task 5 is the behavioural step). Net diff is small and the call-site in `WgcCapture` only changes from `WgcFrameConverter.Convert(...)` to `frameConverter.Convert(...)`. Spec intent is preserved; the implementation locus is per-capture rather than per-call.
- **Spec deviation: ring location.** Spec says "Allocate a small ring of NV12 textures inside `WgcCapture`" — done as 3 raw `nint` pointers in an array, plus a next-slot counter. The simpler "`List<nint>`" or a "`Nv12TextureRing` class" would also work; the array approach minimizes allocations and matches the codebase's existing `[FRAMECOUNT]` style of low-level direct-pointer usage. If the implementing agent prefers a tiny `Nv12TextureRing` wrapper class for testability, that's an acceptable equivalent — the contract is "3 NV12 textures, cycle through them, recreate on resize, dispose with the capture."
- **Coverage gate.** All M3 production code is `#if WINDOWS`-guarded and the unit project targets bare `net8.0`, so the new code is excluded from coverage measurement and the gate stays green at the M2-relaxed 90/85 thresholds. If the implementing agent finds a non-Windows production-code path that changed, they should add a unit test rather than lower the gate further. Coverage backfill on the v2 hosting layer remains a separate concern (memory `project_coverage_gate_red_on_main.md`).
- **End-to-end demo broken — verified intentional.** Task 5 step 4 explicitly notes that `WgcCaptureSourceSmokeTests` should still pass (the smoke is at the capture level, not encoder), but a manual `windowstream serve` would crash because `FFmpegNvencEncoder` reads `frame.pixelBuffer`. Spec section "Migration milestones" says: "the immediately following milestone (M4) must restore it. We never accumulate brokenness across multiple milestones." M4 is the next milestone and restores end-to-end functionality.
- **Atomic-vs-bisectable history.** Task 4 deliberately leaves the new ring/converter fields unused (they get used in Task 5's modified `WgcFrameConverter`). Tasks 4 and 5 commit together to keep the history readable while still allowing the structural refactor (Task 3) to commit on its own. This matches the M1 plan's bisect-friendly approach.
- **Type consistency.** `D3D11VideoProcessorColorConverter`, `Direct3D11DeviceManager`, `Convert`, `Dispose`, `nv12RingTexturePointers`, `colorConverter`, `EnsureNv12RingAndConverter`, `AcquireNv12Slot`, `AcquireNv12SlotDelegate` are used identically across Tasks 1–5. `nint` (not `IntPtr`) for native pointers, matching `Direct3D11DeviceManager` and `FFmpegNvencEncoder` conventions.
- **No placeholders.** Every step has either runnable code, a runnable command with explicit expected output, or a concrete decision (e.g., Task 2 step 2's tolerance-bump policy with the colour-matrix-mismatch reasoning).
- **Identifier discipline.** Full words throughout: `sourceWidthPixels`, `nativeTexturePointer`, `colorConverter`, `enumerator`. No `cfg`, `tex`, `ctx`.
- **API verification escape hatch.** Task 1 step 1 includes explicit guidance for the implementing agent if a Silk.NET 2.22.0 binding name diverges from what's shown — query the package via `Reflection.Assembly`, substitute the binding's actual name, do not change the algorithm. This is the right boundary: the plan owns the algorithm; the binding owns the spelling.
