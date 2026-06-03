using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Core.Encode.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Encode.Testing;

public sealed class FakeVideoEncoderTests
{
    static CapturedFrame SampleFrame() =>
        new CapturedFrame(2, 2, 8, PixelFormat.Bgra32, 100, new byte[16]);

    [Fact]
    public async Task EncodeAsync_BeforeConfigure_Throws()
    {
        await using var encoder = new FakeVideoEncoder();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encoder.EncodeAsync(SampleFrame(), CancellationToken.None));
    }

    [Fact]
    public async Task EncodeAsync_EmitsOneChunkPerFrame()
    {
        await using var encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        encoder.CompleteEncoding();

        var chunks = new List<EncodedChunk>();
        await foreach (var chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task RequestKeyframe_MarksNextChunkAsKeyframe()
    {
        await using var encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        encoder.RequestKeyframe();
        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        encoder.CompleteEncoding();

        var chunks = new List<EncodedChunk>();
        await foreach (var chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Single(chunks);
        Assert.True(chunks[0].IsKeyframe);
    }

    [Fact]
    public async Task Configure_Twice_Throws()
    {
        await using var encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));
        Assert.Throws<InvalidOperationException>(() =>
            encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2)));
    }

    [Fact]
    public async Task Configure_Null_Throws()
    {
        await using var encoder = new FakeVideoEncoder();
        Assert.Throws<ArgumentNullException>(() => encoder.Configure(null!));
    }

    [Fact]
    public async Task EncodeAsync_HonorsCancellation()
    {
        await using var encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            encoder.EncodeAsync(SampleFrame(), cancellation.Token));
    }

    [Fact]
    public async Task RequestKeyframe_BeforeConfigure_Throws()
    {
        await using var encoder = new FakeVideoEncoder();
        Assert.Throws<InvalidOperationException>(() => encoder.RequestKeyframe());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsNoThrow()
    {
        var encoder = new FakeVideoEncoder();
        await encoder.DisposeAsync();
        await encoder.DisposeAsync();
    }

    [Fact]
    public async Task Stopped_ReflectsDisposeState()
    {
        var encoder = new FakeVideoEncoder();
        Assert.False(encoder.Stopped);
        await encoder.DisposeAsync();
        Assert.True(encoder.Stopped);
    }

    [Fact]
    public async Task EncodeAsync_AcceptsTextureRepresentationFrame()
    {
        await using var encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        var textureFrame = CapturedFrame.FromTexture(
            widthPixels: 2,
            heightPixels: 2,
            rowStrideBytes: 2,
            pixelFormat: PixelFormat.Nv12,
            presentationTimestampMicroseconds: 12_345,
            nativeTexturePointer: 0x12345678,
            textureArrayIndex: 0);

        await encoder.EncodeAsync(textureFrame, CancellationToken.None);
        encoder.CompleteEncoding();

        var chunks = new List<EncodedChunk>();
        await foreach (var chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Single(chunks);
        Assert.Equal(12_345L, chunks[0].PresentationTimestampMicroseconds);
    }
}
