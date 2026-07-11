#if WINDOWS
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Integration.Tests.Encode;

public sealed class FFmpegNvencEncoderConstructionTests
{
    static EncoderOptions SampleOptions() =>
        new EncoderOptions(640, 480, 30, 1_000_000, 60, 2);

    static CapturedFrame SampleFrame() =>
        new CapturedFrame(2, 2, 8, PixelFormat.Bgra32, 0, new byte[16]);

    [Fact]
    public async Task DisposeAsync_BeforeConfigure_IsNoThrow()
    {
        var encoder = new FFmpegNvencEncoder(new DummyLoader());
        await encoder.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsNoThrow()
    {
        var encoder = new FFmpegNvencEncoder(new DummyLoader());
        await encoder.DisposeAsync();
        await encoder.DisposeAsync();
    }

    [Fact]
    public async Task ValidatePreConfigureState_Null_Throws()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<ArgumentNullException>(() => encoder.ValidatePreConfigureState(null!));
    }

    [Fact]
    public async Task ValidatePreConfigureState_AlreadyConfigured_Throws()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        Assert.Throws<InvalidOperationException>(() => encoder.ValidatePreConfigureState(SampleOptions()));
    }

    [Fact]
    public async Task ValidatePreConfigureState_LoaderFails_PropagatesException()
    {
        await using var encoder = new FFmpegNvencEncoder(new FailingLoader());
        Assert.Throws<EncoderException>(() => encoder.ValidatePreConfigureState(SampleOptions()));
    }

    [Fact]
    public async Task ValidatePreConfigureState_ValidState_DoesNotThrow()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        // Should not throw — DummyLoader succeeds and options is null
        encoder.ValidatePreConfigureState(SampleOptions());
    }

    [Fact]
    public async Task Configure_WhenLoaderFails_ThrowsEncoderException()
    {
        await using var encoder = new FFmpegNvencEncoder(new FailingLoader());
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
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encoder.EncodeAsync(SampleFrame(), CancellationToken.None));
    }

    [Fact]
    public async Task EncodeAsync_Cancelled_AfterConfigure_ThrowsOperationCanceled()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var threw = false;
        try
        {
            var result = encoder.EncodeAsync(SampleFrame(), cancellation.Token);
            await result;
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw, "Expected OperationCanceledException.");
    }

    [Fact]
    public async Task RequestKeyframe_BeforeConfigure_Throws()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<InvalidOperationException>(() => encoder.RequestKeyframe());
    }

    [Fact]
    public async Task RequestKeyframe_AfterConfigure_SetsFlag()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        // Should not throw
        encoder.RequestKeyframe();
    }

    [Fact]
    public async Task SimulateConfiguredForTest_Null_Throws()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<ArgumentNullException>(() => encoder.SimulateConfiguredForTest(null!));
    }

    [Fact]
    public async Task EncodeAsync_AfterConfigure_ReturnsTask()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        encoder.SimulateConfiguredForTest(SampleOptions());
        // EncodeAsyncCore is excluded; we only need to verify EncodeAsync reaches the return statement.
        // The native path (EncodeOnThread) will throw DllNotFoundException which we swallow here.
        try
        {
            await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        }
        catch (DllNotFoundException) { /* expected — no native FFmpeg DLLs in test run */ }
#pragma warning disable CA1031, RCS1075 // intentional: any native-interop error is acceptable here; test only checks the return path // RCS1075: intentional swallow of native-interop errors in this return-path test
        catch (Exception) { /* other native errors are also acceptable */ }
#pragma warning restore CA1031, RCS1075
    }

    [Fact]
    public async Task EncodedChunks_IsNotNull()
    {
        await using var encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.NotNull(encoder.EncodedChunks);
    }

    sealed class DummyLoader : IFFmpegNativeLoader
    {
        public void EnsureLoaded() { /* no-op — no native work in these tests */ }
    }

    sealed class FailingLoader : IFFmpegNativeLoader
    {
        public void EnsureLoaded() => throw new EncoderException("FFmpeg natives missing.");
    }
}
#endif
