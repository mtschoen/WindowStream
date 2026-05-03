#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using Silk.NET.Direct3D11;
using SilkDxgi = Silk.NET.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace WindowStream.Core.Capture.Windows;

public sealed class WgcCapture : IWindowCapture
{
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;
    private readonly Channel<object> frameChannel =
        Channel.CreateBounded<object>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly CancellationToken cancellationToken;
    private readonly long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    private bool disposed;

    public WindowHandle handle { get; }
    public CaptureOptions options { get; }
    public IAsyncEnumerable<CapturedFrame> Frames { get; }

    private readonly Direct3D11DeviceManager deviceManager;
    private readonly WgcFrameConverter frameConverter;

    // NV12 ring — 3 individual NV12 textures allocated lazily and recreated on resize.
    private const int RingSize = 3;
    private readonly nint[] nativeNv12TexturePointers = new nint[RingSize];
    private int nextRingSlot;
    private int ringWidth;
    private int ringHeight;
    private D3D11VideoProcessorColorConverter? colorConverter;

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

        frameConverter = new WgcFrameConverter(AcquireNv12Slot);

        item.Closed += OnItemClosed;
        // Use CreateFreeThreaded so FrameArrived fires on any thread without a DispatcherQueue
        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            deviceManager.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: item.Size);
        framePool.FrameArrived += OnFrameArrived;
        session = framePool.CreateCaptureSession(item);
        session.StartCapture();

        Frames = ReadAsync();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        frameChannel.Writer.TryComplete(new WindowGoneException(handle));
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        try
        {
            using Direct3D11CaptureFrame frame = pool.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }
            CapturedFrame converted = frameConverter.Convert(frame, startTicks);
            frameChannel.Writer.TryWrite(converted);
        }
        catch (Exception exception)
        {
            frameChannel.Writer.TryComplete(new WindowCaptureException("WGC frame conversion failed.", exception));
        }
    }

    private async IAsyncEnumerable<CapturedFrame> ReadAsync(
        [EnumeratorCancellation] CancellationToken enumeratorCancellation = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, enumeratorCancellation);
        while (await frameChannel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            while (frameChannel.Reader.TryRead(out object? next))
            {
                if (next is CapturedFrame frame)
                {
                    yield return frame;
                }
                else if (next is Exception exception)
                {
                    throw exception;
                }
            }
        }
    }

    /// <summary>
    /// Lazily initialises (or re-initialises on dimension change) the 3-element NV12 texture
    /// ring and the <see cref="D3D11VideoProcessorColorConverter"/>. Called from
    /// <see cref="AcquireNv12Slot"/> inside the WGC <c>FrameArrived</c> callback.
    /// </summary>
    private unsafe void EnsureNv12RingAndConverter(int width, int height)
    {
        if (colorConverter is not null && ringWidth == width && ringHeight == height)
        {
            return;
        }

        // Dimensions changed (or first call) — dispose existing ring and converter.
        DisposeNv12RingAndConverter();

        ID3D11Device* device = (ID3D11Device*)deviceManager.NativeDevicePointer;

        Texture2DDesc description = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = SilkDxgi.Format.FormatNV12,
            SampleDesc = new SilkDxgi.SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.RenderTarget,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        for (int slotIndex = 0; slotIndex < RingSize; slotIndex++)
        {
            ID3D11Texture2D* texture = null;
            int hresult = device->CreateTexture2D(ref description, (SubresourceData*)null, ref texture);
            if (hresult < 0)
            {
                // Release any textures already allocated before throwing.
                for (int releaseIndex = 0; releaseIndex < slotIndex; releaseIndex++)
                {
                    if (nativeNv12TexturePointers[releaseIndex] != 0)
                    {
                        ((ID3D11Texture2D*)nativeNv12TexturePointers[releaseIndex])->Release();
                        nativeNv12TexturePointers[releaseIndex] = 0;
                    }
                }
                throw new WindowCaptureException(
                    "CreateTexture2D (NV12 ring) failed. HRESULT: 0x"
                    + ((uint)hresult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
            nativeNv12TexturePointers[slotIndex] = (nint)texture;
        }

        colorConverter = new D3D11VideoProcessorColorConverter(deviceManager, width, height);
        ringWidth = width;
        ringHeight = height;
        nextRingSlot = 0;
    }

    /// <summary>
    /// Disposes the NV12 ring textures and the colour converter, resetting ring state.
    /// </summary>
    private unsafe void DisposeNv12RingAndConverter()
    {
        colorConverter?.Dispose();
        colorConverter = null;

        for (int slotIndex = 0; slotIndex < RingSize; slotIndex++)
        {
            if (nativeNv12TexturePointers[slotIndex] != 0)
            {
                ((ID3D11Texture2D*)nativeNv12TexturePointers[slotIndex])->Release();
                nativeNv12TexturePointers[slotIndex] = 0;
            }
        }

        ringWidth = 0;
        ringHeight = 0;
        nextRingSlot = 0;
    }

    /// <summary>
    /// Acquires the next NV12 ring slot after ensuring the ring and converter are initialised
    /// for the given dimensions. Returns the texture pointer, array index (always 0 for the
    /// M3 hand-rolled ring), and the active colour converter.
    /// </summary>
    private (nint texturePointer, int arrayIndex, D3D11VideoProcessorColorConverter converter) AcquireNv12Slot(
        int width, int height)
    {
        EnsureNv12RingAndConverter(width, height);
        int slot = nextRingSlot;
        nextRingSlot = (nextRingSlot + 1) % RingSize;
        return (nativeNv12TexturePointers[slot], 0, colorConverter!);
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }
        disposed = true;
        try { session.Dispose(); } catch { }
        try { framePool.Dispose(); } catch { }
        DisposeNv12RingAndConverter();
        try { deviceManager.Dispose(); } catch { }
        frameChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
#endif
