using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Core.Tests.Encode;

public sealed class FFmpegNvencEncoderConstructionTests
{
    [Fact]
    public async Task DisposeAsync_BeforeConfigure_IsNoThrow()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        await encoder.DisposeAsync();
    }

    [Fact]
    public void Configure_Null_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<ArgumentNullException>(() => encoder.Configure(null!));
    }

    [Fact]
    public async Task EncodeAsync_BeforeConfigure_Throws()
    {
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        CapturedFrame frame = new CapturedFrame(2, 2, 8, PixelFormat.Bgra32, 0, new byte[16]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encoder.EncodeAsync(frame, CancellationToken.None));
    }

    [Fact]
    public void RequestKeyframe_BeforeConfigure_Throws()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new DummyLoader());
        Assert.Throws<InvalidOperationException>(() => encoder.RequestKeyframe());
    }

    [Fact]
    public void Configure_WhenCodecMissing_ThrowsEncoderException()
    {
        FFmpegNvencEncoder encoder = new FFmpegNvencEncoder(new FailingLoader());
        Assert.Throws<EncoderException>(() =>
            encoder.Configure(new EncoderOptions(640, 480, 30, 1_000_000, 60, 2)));
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
