#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
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
            CapturedFrame converted = WgcFrameConverter.Convert(frame, startTicks);
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
}
#endif
