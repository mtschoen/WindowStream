#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Silk.NET.Direct3D11;
using WindowStream.Core.Encode;
using SilkDxgi = Silk.NET.DXGI;

namespace WindowStream.Core.Capture.Windows;

public sealed class WgcCapture : IWindowCapture
{
    // Retained to keep the WGC capture item alive for the session's lifetime so its
    // Closed event keeps firing; the session does not own a managed reference to it.
    // ReSharper disable once NotAccessedField.Local
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _framePool;
    readonly GraphicsCaptureSession _session;

    readonly Channel<object> _frameChannel =
        Channel.CreateBounded<object>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    readonly CancellationToken _cancellationToken;
    readonly long _startTicks = Stopwatch.GetTimestamp();
    bool _disposed;

    public WindowHandle Handle { get; }
    public CaptureOptions Options { get; }
    public IAsyncEnumerable<CapturedFrame> Frames { get; }

    readonly Direct3D11DeviceManager _deviceManager;
    readonly WgcFrameConverter _frameConverter;
    readonly bool _ownsDeviceManager;
    readonly IFrameTexturePool? _sharedFrameTexturePool;

    // NV12 ring — 3 individual NV12 textures allocated lazily and recreated on resize.
    const int RingSize = 3;
    readonly nint[] _nativeNv12TexturePointers = new nint[RingSize];
    int _nextRingSlot;
    int _ringWidth;
    int _ringHeight;
    D3D11VideoProcessorColorConverter? _colorConverter;

    public WgcCapture(
        WindowHandle handle,
        CaptureOptions options,
        GraphicsCaptureItem item,
        Direct3D11DeviceManager deviceManager,
        bool ownsDeviceManager,
        IFrameTexturePool? sharedFrameTexturePool,
        CancellationToken cancellationToken)
    {
        Handle = handle;
        Options = options;
        _item = item;
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _ownsDeviceManager = ownsDeviceManager;
        _sharedFrameTexturePool = sharedFrameTexturePool;
        _cancellationToken = cancellationToken;

        _frameConverter = new WgcFrameConverter(AcquireNv12Slot);

        item.Closed += OnItemClosed;
        // Use CreateFreeThreaded so FrameArrived fires on any thread without a DispatcherQueue
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            deviceManager.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        _session.StartCapture();

        Frames = ReadAsync(cancellationToken);
    }

    void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _frameChannel.Writer.TryComplete(new WindowGoneException(Handle));
    }

    void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }
            var converted = _frameConverter.Convert(frame, _startTicks);
            _frameChannel.Writer.TryWrite(converted);
        }
#pragma warning disable CA1031 // top-level WGC frame-arrived callback; exception is forwarded to channel as completion fault
        catch (Exception exception)
        {
            _frameChannel.Writer.TryComplete(new WindowCaptureException("WGC frame conversion failed.", exception));
        }
#pragma warning restore CA1031
    }

    async IAsyncEnumerable<CapturedFrame> ReadAsync(
        [EnumeratorCancellation] CancellationToken enumeratorCancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken, enumeratorCancellation);
        while (await _frameChannel.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
        {
            while (_frameChannel.Reader.TryRead(out var next))
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
    /// Lazily initializes (or re-initializes on dimension change) the 3-element NV12 texture
    /// ring and the <see cref="D3D11VideoProcessorColorConverter"/>. Called from
    /// <see cref="AcquireNv12Slot"/> inside the WGC <c>FrameArrived</c> callback.
    /// </summary>
    unsafe D3D11VideoProcessorColorConverter EnsureNv12RingAndConverter(int width, int height)
    {
        // NV12 requires both texture dimensions to be even (chroma plane is
        // height/2 rows × width bytes; D3D11 CreateTexture2D rejects odd
        // dimensions with E_INVALIDARG). WGC frame dimensions track the
        // captured window's client area in physical pixels, which can be odd
        // at noninteger DPI scales (e.g. 175% on a 600×500 window → 1050×875).
        var evenWidth = RoundUpToEven(width);
        var evenHeight = RoundUpToEven(height);

        if (_colorConverter is not null && _ringWidth == evenWidth && _ringHeight == evenHeight)
        {
            return _colorConverter;
        }

        // Dimensions changed (or first call) — dispose existing ring and converter.
        DisposeNv12RingAndConverter();

        var device = (ID3D11Device*)_deviceManager.NativeDevicePointer;

        var description = new Texture2DDesc
        {
            Width = (uint)evenWidth,
            Height = (uint)evenHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = SilkDxgi.Format.FormatNV12,
            SampleDesc = new SilkDxgi.SampleDesc { Count = 1, Quality = 0 },
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.RenderTarget,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        for (var slotIndex = 0; slotIndex < RingSize; slotIndex++)
        {
            ID3D11Texture2D* texture = null;
            var hresult = device->CreateTexture2D(in description, null, ref texture);
            if (hresult < 0)
            {
                // Release any textures already allocated before throwing.
                for (var releaseIndex = 0; releaseIndex < slotIndex; releaseIndex++)
                {
                    if (_nativeNv12TexturePointers[releaseIndex] != 0)
                    {
                        ((ID3D11Texture2D*)_nativeNv12TexturePointers[releaseIndex])->Release();
                        _nativeNv12TexturePointers[releaseIndex] = 0;
                    }
                }
                throw new WindowCaptureException(
                    "CreateTexture2D (NV12 ring) failed. HRESULT: 0x"
                    + hresult.ToString("X8", CultureInfo.InvariantCulture));
            }
            _nativeNv12TexturePointers[slotIndex] = (nint)texture;
        }

        _colorConverter = new D3D11VideoProcessorColorConverter(_deviceManager, evenWidth, evenHeight);
        _ringWidth = evenWidth;
        _ringHeight = evenHeight;
        _nextRingSlot = 0;
        return _colorConverter;
    }

    static int RoundUpToEven(int value) => (value + 1) & ~1;

    /// <summary>
    /// Disposes the NV12 ring textures and the color converter, resetting ring state.
    /// </summary>
    unsafe void DisposeNv12RingAndConverter()
    {
        _colorConverter?.Dispose();
        _colorConverter = null;

        for (var slotIndex = 0; slotIndex < RingSize; slotIndex++)
        {
            if (_nativeNv12TexturePointers[slotIndex] != 0)
            {
                ((ID3D11Texture2D*)_nativeNv12TexturePointers[slotIndex])->Release();
                _nativeNv12TexturePointers[slotIndex] = 0;
            }
        }

        _ringWidth = 0;
        _ringHeight = 0;
        _nextRingSlot = 0;
    }

    /// <summary>
    /// Lazily initializes (or re-initializes on dimension change) only the
    /// <see cref="D3D11VideoProcessorColorConverter"/>, without allocating the NV12 ring.
    /// Used when an external <see cref="IFrameTexturePool"/> supplies the destination textures.
    /// </summary>
    D3D11VideoProcessorColorConverter EnsureColorConverter(int width, int height)
    {
        // Round to even to mirror EnsureNv12RingAndConverter — the M4 path's
        // pool textures are encoder-sized (already even in practice), but the
        // converter's content desc should match the texture exactly.
        var evenWidth = RoundUpToEven(width);
        var evenHeight = RoundUpToEven(height);

        if (_colorConverter is not null && _ringWidth == evenWidth && _ringHeight == evenHeight)
        {
            return _colorConverter;
        }

#pragma warning disable CA1031 // best-effort dispose of old converter before recreating; failure is non-fatal
        try { _colorConverter?.Dispose(); } catch { /* best-effort dispose before recreate; failure is non-fatal */ }
#pragma warning restore CA1031
        _colorConverter = new D3D11VideoProcessorColorConverter(_deviceManager, evenWidth, evenHeight);
        _ringWidth = evenWidth;
        _ringHeight = evenHeight;
        return _colorConverter;
    }

    /// <summary>
    /// Acquires the next NV12 destination texture and the active color converter.
    /// When an external <see cref="IFrameTexturePool"/> was supplied (M4 path), the
    /// texture comes from the encoder's <c>hw_frames_ctx</c> pool; otherwise the
    /// M3 hand-rolled ring is used.
    /// Returns the texture pointer, array index, and converter.
    /// </summary>
    (nint texturePointer, int arrayIndex, D3D11VideoProcessorColorConverter converter) AcquireNv12Slot(
        int width, int height)
    {
        if (_sharedFrameTexturePool is not null)
        {
            // M4 path: NV12 textures come from the encoder's hw_frames_ctx pool.
            var converter = EnsureColorConverter(width, height);
            _sharedFrameTexturePool.AcquireFrameTexture(out var poolTexturePointer, out var poolSubresourceIndex);
            return (poolTexturePointer, poolSubresourceIndex, converter);
        }

        // M3 fallback path: hand-rolled NV12 ring inside this capture.
        var ringConverter = EnsureNv12RingAndConverter(width, height);
        var slot = _nextRingSlot;
        _nextRingSlot = (_nextRingSlot + 1) % RingSize;
        return (_nativeNv12TexturePointers[slot], 0, ringConverter);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
#pragma warning disable CA1031 // best-effort dispose in async teardown; failures must not propagate from DisposeAsync
        try { _session.Dispose(); } catch { /* best-effort teardown; must not propagate from DisposeAsync */ }
        try { _framePool.Dispose(); } catch { /* best-effort teardown; must not propagate from DisposeAsync */ }
#pragma warning restore CA1031
        DisposeNv12RingAndConverter();
        if (_ownsDeviceManager)
        {
#pragma warning disable CA1031 // best-effort dispose in async teardown; failures must not propagate from DisposeAsync
            try { _deviceManager.Dispose(); } catch { /* best-effort teardown; must not propagate from DisposeAsync */ }
#pragma warning restore CA1031
        }
        _frameChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
#endif
