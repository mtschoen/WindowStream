using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Testing;

public sealed class FakeWindowCaptureSourceTests
{
    [Fact]
    public void ListWindows_ReturnsConfiguredEntries()
    {
        var source = new FakeWindowCaptureSource(
            new[]
            {
                new WindowInformation(new WindowHandle(1), "Notepad", "notepad.exe", 640, 480),
                new WindowInformation(new WindowHandle(2), "VS", "devenv.exe", 1920, 1080),
            });

        var list = source.ListWindows().ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("Notepad", list[0].Title);
    }

    [Fact]
    public void Start_UnknownHandle_ThrowsWindowGone()
    {
        var source = new FakeWindowCaptureSource(Array.Empty<WindowInformation>());
        Assert.Throws<WindowGoneException>(() =>
            source.Start(new WindowHandle(99), new CaptureOptions(60, false), CancellationToken.None));
    }

    [Fact]
    public async Task Start_EmitsConfiguredFrames_ThenCompletes()
    {
        var window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        var source = new FakeWindowCaptureSource(new[] { window });
        source.EnqueueFrame(window.Handle, BuildSolidFrame(4, 2, 0x11));
        source.EnqueueFrame(window.Handle, BuildSolidFrame(4, 2, 0x22));
        source.CompleteAfterEnqueued(window.Handle);

        await using var capture = source.Start(
            window.Handle, new CaptureOptions(60, false), CancellationToken.None);

        var collected = new List<CapturedFrame>();
        await foreach (var frame in capture.Frames.WithCancellation(CancellationToken.None))
        {
            collected.Add(frame);
        }
        Assert.Equal(2, collected.Count);
        Assert.Equal(0x11, collected[0].PixelBuffer.Span[0]);
        Assert.Equal(0x22, collected[1].PixelBuffer.Span[0]);
    }

    [Fact]
    public async Task Start_HonorsCancellation()
    {
        var window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        var source = new FakeWindowCaptureSource(new[] { window });
        using var cancellation = new CancellationTokenSource();
        await using var capture = source.Start(
            window.Handle, new CaptureOptions(60, false), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in capture.Frames.WithCancellation(cancellation.Token)) { }
        });
    }

    [Fact]
    public async Task Start_WindowGoneMidStream_ThrowsWindowGone()
    {
        var window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        var source = new FakeWindowCaptureSource(new[] { window });
        source.EnqueueFrame(window.Handle, BuildSolidFrame(4, 2, 0x33));
        source.FaultAfterEnqueued(window.Handle, new WindowGoneException(window.Handle));

        await using var capture = source.Start(
            window.Handle, new CaptureOptions(60, false), CancellationToken.None);

        await Assert.ThrowsAsync<WindowGoneException>(async () =>
        {
            await foreach (var _ in capture.Frames) { }
        });
    }

    [Fact]
    public void GetCapture_ReturnsNull_WhenHandleNotStarted()
    {
        var source = new FakeWindowCaptureSource(Array.Empty<WindowInformation>());
        var capture = source.GetCapture(new WindowHandle(999));
        Assert.Null(capture);
    }

    [Fact]
    public void GetCapture_ReturnsCapture_AfterStart()
    {
        var window = new WindowInformation(new WindowHandle(1), "W", "p", 4, 2);
        var source = new FakeWindowCaptureSource(new[] { window });
        source.Start(window.Handle, new CaptureOptions(30, false), CancellationToken.None);
        var capture = source.GetCapture(window.Handle);
        Assert.NotNull(capture);
    }

    static CapturedFrame BuildSolidFrame(int width, int height, byte value)
    {
        var buffer = new byte[width * 4 * height];
        Array.Fill(buffer, value);
        return new CapturedFrame(width, height, width * 4, PixelFormat.Bgra32, 0, buffer);
    }
}
