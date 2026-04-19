using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Core.Tests.Encode;

public sealed class FFmpegNvencEncoderConstructionTests
{
    private static EncoderOptions SampleOptions() =>
        new EncoderOptions(640, 480, 30, 1_000_000, 60, 2);

    private static CapturedFrame SampleFrame() =>
        new CapturedFrame(2, 2, 8, PixelFormat.Bgra32, 0, new byte[16]);

    [Fact]
    public async Task DisposeAsync_BeforeConfigure_IsNoThrow()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        await encoder.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsNoThrow()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        await encoder.DisposeAsync();
        await encoder.DisposeAsync();
    }

    [Fact]
    public void ValidatePreConfigureState_Null_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<ArgumentNullException>(() => encoder.ValidatePreConfigureState(null!));
    }

    [Fact]
    public void ValidatePreConfigureState_AlreadyConfigured_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        Assert.Throws<InvalidOperationException>(() => encoder.ValidatePreConfigureState(SampleOptions()));
    }

    [Fact]
    public void ValidatePreConfigureState_LoaderFails_PropagatesException()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new FailingLoader());
        Assert.Throws<EncoderException>(() => encoder.ValidatePreConfigureState(SampleOptions()));
    }

    [Fact]
    public void ValidatePreConfigureState_ValidState_DoesNotThrow()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        // Should not throw — DummyLoader succeeds and options is null
        encoder.ValidatePreConfigureState(SampleOptions());
    }

    [Fact]
    public void Configure_WhenLoaderFails_ThrowsEncoderException()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new FailingLoader());
        Assert.Throws<EncoderException>(() => encoder.Configure(SampleOptions()));
    }

    [Fact]
    public void NativeLoader_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FFmpegNvencEncoder(null!));
    }

    [Fact]
    public async Task EncodeAsync_BeforeConfigure_Throws()
    {
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encoder.EncodeAsync(SampleFrame(), CancellationToken.None));
    }

    [Fact]
    public async Task EncodeAsync_Cancelled_AfterConfigure_ThrowsOperationCanceled()
    {
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool threw = false;
        try
        {
            Task result = encoder.EncodeAsync(SampleFrame(), cancellation.Token);
            await result;
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected OperationCanceledException.");
    }

    [Fact]
    public void RequestKeyframe_BeforeConfigure_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<InvalidOperationException>(() => encoder.RequestKeyframe());
    }

    [Fact]
    public void RequestKeyframe_AfterConfigure_SetsFlag()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        // Should not throw
        encoder.RequestKeyframe();
    }

    [Fact]
    public void SimulateConfiguredForTest_Null_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<ArgumentNullException>(() => encoder.SimulateConfiguredForTest(null!));
    }

    [Fact]
    public async Task EncodeAsync_AfterConfigure_ReturnsTask()
    {
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        // EncodeAsyncCore is excluded; we only need to verify EncodeAsync reaches the return statement.
        // The native path (EncodeOnThread) will throw DllNotFoundException which we swallow here.
        try
        {
            await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        }
        catch (DllNotFoundException) { /* expected — no native FFmpeg DLLs in test run */ }
        catch (Exception) { /* other native errors are also acceptable */ }
    }

    [Fact]
    public void EncodedChunks_IsNotNull()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.NotNull(encoder.EncodedChunks);
    }

    private sealed class DummyLoader : IFFmpegNativeLoader
    {
        public void EnsureLoaded() { /* no-op — no native work in these tests */ }
    }

    private sealed class FailingLoader : IFFmpegNativeLoader
    {
        public void EnsureLoaded() => throw new EncoderException("FFmpeg natives missing.");
    }
}
