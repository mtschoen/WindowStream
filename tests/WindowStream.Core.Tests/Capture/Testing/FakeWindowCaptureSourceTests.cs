using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Testing;

public sealed class FakeWindowCaptureSourceTests
{
    [Fact]
    public void ListWindows_ReturnsConfiguredEntries()
    {
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(
            new[]
            {
                new WindowInformation(new WindowHandle(1), "Notepad", "notepad.exe", 640, 480),
                new WindowInformation(new WindowHandle(2), "VS", "devenv.exe", 1920, 1080),
            });

        List<WindowInformation> list = source.ListWindows().ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("Notepad", list[0].title);
    }

    [Fact]
    public void Start_UnknownHandle_ThrowsWindowGone()
    {
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(System.Array.Empty<WindowInformation>());
        Assert.Throws<WindowGoneException>(() =>
            source.Start(new WindowHandle(99), new CaptureOptions(60, false), CancellationToken.None));
    }

    [Fact]
    public async Task Start_EmitsConfiguredFrames_ThenCompletes()
    {
        WindowInformation window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(new[] { window });
        source.EnqueueFrame(window.handle, BuildSolidFrame(4, 2, 0x11));
        source.EnqueueFrame(window.handle, BuildSolidFrame(4, 2, 0x22));
        source.CompleteAfterEnqueued(window.handle);

        await using IWindowCapture capture = source.Start(
            window.handle, new CaptureOptions(60, false), CancellationToken.None);

        List<CapturedFrame> collected = new List<CapturedFrame>();
        await foreach (CapturedFrame frame in capture.Frames.WithCancellation(CancellationToken.None))
        {
            collected.Add(frame);
        }
        Assert.Equal(2, collected.Count);
        Assert.Equal(0x11, collected[0].pixelBuffer.Span[0]);
        Assert.Equal(0x22, collected[1].pixelBuffer.Span[0]);
    }

    [Fact]
    public async Task Start_HonorsCancellation()
    {
        WindowInformation window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(new[] { window });
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        await using IWindowCapture capture = source.Start(
            window.handle, new CaptureOptions(60, false), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (CapturedFrame _ in capture.Frames.WithCancellation(cancellation.Token)) { }
        });
    }

    [Fact]
    public async Task Start_WindowGoneMidStream_ThrowsWindowGone()
    {
        WindowInformation window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(new[] { window });
        source.EnqueueFrame(window.handle, BuildSolidFrame(4, 2, 0x33));
        source.FaultAfterEnqueued(window.handle, new WindowGoneException(window.handle));

        await using IWindowCapture capture = source.Start(
            window.handle, new CaptureOptions(60, false), CancellationToken.None);

        await Assert.ThrowsAsync<WindowGoneException>(async () =>
        {
            await foreach (CapturedFrame _ in capture.Frames) { }
        });
    }

    private static CapturedFrame BuildSolidFrame(int width, int height, byte value)
    {
        byte[] buffer = new byte[width * 4 * height];
        System.Array.Fill(buffer, value);
        return new CapturedFrame(width, height, width * 4, PixelFormat.Bgra32, 0, buffer);
    }
}
