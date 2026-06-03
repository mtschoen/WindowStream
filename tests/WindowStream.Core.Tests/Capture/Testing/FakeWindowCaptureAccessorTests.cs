using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Testing;

public sealed class FakeWindowCaptureAccessorTests
{
    [Fact]
    public void CaptureOptions_ExposesTargetFramesPerSecond()
    {
        var options = new CaptureOptions(TargetFramesPerSecond: 30, IncludeCursor: true);
        Assert.Equal(30, options.TargetFramesPerSecond);
        Assert.True(options.IncludeCursor);
    }

    [Fact]
    public async Task FakeWindowCapture_ExposesHandleAndOptions()
    {
        var handle = new WindowHandle(123);
        var options = new CaptureOptions(25, false);
        await using var capture = new FakeWindowCapture(handle, options, CancellationToken.None);
        Assert.Equal(handle, capture.Handle);
        Assert.Equal(options, capture.Options);
    }

    [Fact]
    public async Task FakeWindowCapture_SentinelObject_BreaksIteration()
    {
        var handle = new WindowHandle(1);
        var options = new CaptureOptions(30, false);
        await using var capture = new FakeWindowCapture(handle, options, CancellationToken.None);

        // Write a sentinel object (neither CapturedFrame nor Exception) to trigger yield break
        capture._channel.Writer.TryWrite(new object());

        var collected = new List<CapturedFrame>();
        await foreach (var frame in capture.Frames)
        {
            collected.Add(frame);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public void FakeWindowCaptureSource_NullWindows_DefaultsToEmpty()
    {
        // Constructor with null windows should not throw and ListWindows returns empty
        var source = new FakeWindowCaptureSource(null);
        Assert.Empty(source.ListWindows());
    }

    [Fact]
    public async Task FakeWindowCapture_DisposeAsync_CompletesChannel()
    {
        var handle = new WindowHandle(5);
        var options = new CaptureOptions(60, false);
        var capture = new FakeWindowCapture(handle, options, CancellationToken.None);

        await capture.DisposeAsync();

        // After dispose, channel should be completed — iteration should finish immediately
        var collected = new List<CapturedFrame>();
        await foreach (var frame in capture.Frames)
        {
            collected.Add(frame);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public async Task FakeWindowCapture_ExceptionWrittenAsValue_IsRethrownDuringIteration()
    {
        var handle = new WindowHandle(9);
        var options = new CaptureOptions(60, false);
        await using var capture = new FakeWindowCapture(handle, options, CancellationToken.None);

        // Write an Exception directly as a value (not via TryComplete) to cover the
        // "else if (next is Exception)" branch in ReadFramesAsync
        var written = new InvalidOperationException("direct-write");
        capture._channel.Writer.TryWrite(written);
        capture._channel.Writer.TryComplete();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in capture.Frames) { }
        });
    }
}
