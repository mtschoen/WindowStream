using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Testing;

public sealed class FakeWindowCaptureAccessorTests
{
    [Fact]
    public void CaptureOptions_ExposesTargetFramesPerSecond()
    {
        CaptureOptions options = new CaptureOptions(targetFramesPerSecond: 30, includeCursor: true);
        Assert.Equal(30, options.targetFramesPerSecond);
        Assert.True(options.includeCursor);
    }

    [Fact]
    public void FakeWindowCapture_ExposesHandleAndOptions()
    {
        WindowHandle handle = new WindowHandle(123);
        CaptureOptions options = new CaptureOptions(25, false);
        FakeWindowCapture capture = new FakeWindowCapture(handle, options, CancellationToken.None);
        Assert.Equal(handle, capture.handle);
        Assert.Equal(options, capture.options);
    }

    [Fact]
    public async Task FakeWindowCapture_SentinelObject_BreaksIteration()
    {
        WindowHandle handle = new WindowHandle(1);
        CaptureOptions options = new CaptureOptions(30, false);
        FakeWindowCapture capture = new FakeWindowCapture(handle, options, CancellationToken.None);

        // Write a sentinel object (neither CapturedFrame nor Exception) to trigger yield break
        capture.channel.Writer.TryWrite(new object());

        List<CapturedFrame> collected = new List<CapturedFrame>();
        await foreach (CapturedFrame frame in capture.Frames)
        {
            collected.Add(frame);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public void FakeWindowCaptureSource_NullWindows_DefaultsToEmpty()
    {
        // Constructor with null windows should not throw and ListWindows returns empty
        FakeWindowCaptureSource source = new FakeWindowCaptureSource(null!);
        Assert.Empty(source.ListWindows());
    }

    [Fact]
    public async Task FakeWindowCapture_DisposeAsync_CompletesChannel()
    {
        WindowHandle handle = new WindowHandle(5);
        CaptureOptions options = new CaptureOptions(60, false);
        FakeWindowCapture capture = new FakeWindowCapture(handle, options, CancellationToken.None);

        await capture.DisposeAsync();

        // After dispose, channel should be completed — iteration should finish immediately
        List<CapturedFrame> collected = new List<CapturedFrame>();
        await foreach (CapturedFrame frame in capture.Frames)
        {
            collected.Add(frame);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public async Task FakeWindowCapture_ExceptionWrittenAsValue_IsRethrownDuringIteration()
    {
        WindowHandle handle = new WindowHandle(9);
        CaptureOptions options = new CaptureOptions(60, false);
        FakeWindowCapture capture = new FakeWindowCapture(handle, options, CancellationToken.None);

        // Write an Exception directly as a value (not via TryComplete) to cover the
        // "else if (next is Exception)" branch in ReadFramesAsync
        InvalidOperationException written = new InvalidOperationException("direct-write");
        capture.channel.Writer.TryWrite(written);
        capture.channel.Writer.TryComplete();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (CapturedFrame _ in capture.Frames) { }
        });
    }
}
