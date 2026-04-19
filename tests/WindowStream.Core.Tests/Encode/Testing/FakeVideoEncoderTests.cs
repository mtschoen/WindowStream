using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using WindowStream.Core.Encode.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Encode.Testing;

public sealed class FakeVideoEncoderTests
{
    private static CapturedFrame SampleFrame() =>
        new CapturedFrame(2, 2, 8, PixelFormat.Bgra32, 100, new byte[16]);

    [Fact]
    public async Task EncodeAsync_BeforeConfigure_Throws()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            encoder.EncodeAsync(SampleFrame(), CancellationToken.None));
    }

    [Fact]
    public async Task EncodeAsync_EmitsOneChunkPerFrame()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        encoder.CompleteEncoding();

        List<EncodedChunk> chunks = new List<EncodedChunk>();
        await foreach (EncodedChunk chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task RequestKeyframe_MarksNextChunkAsKeyframe()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));

        encoder.RequestKeyframe();
        await encoder.EncodeAsync(SampleFrame(), CancellationToken.None);
        encoder.CompleteEncoding();

        List<EncodedChunk> chunks = new List<EncodedChunk>();
        await foreach (EncodedChunk chunk in encoder.EncodedChunks)
        {
            chunks.Add(chunk);
        }
        Assert.Single(chunks);
        Assert.True(chunks[0].isKeyframe);
    }

    [Fact]
    public void Configure_Twice_Throws()
    {
        FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));
        Assert.Throws<InvalidOperationException>(() =>
            encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2)));
    }

    [Fact]
    public void Configure_Null_Throws()
    {
        FakeVideoEncoder encoder = new FakeVideoEncoder();
        Assert.Throws<ArgumentNullException>(() => encoder.Configure(null!));
    }

    [Fact]
    public async Task EncodeAsync_HonorsCancellation()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        encoder.Configure(new EncoderOptions(2, 2, 30, 1_000_000, 30, 2));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            encoder.EncodeAsync(SampleFrame(), cancellation.Token));
    }

    [Fact]
    public async Task RequestKeyframe_BeforeConfigure_Throws()
    {
        await using FakeVideoEncoder encoder = new FakeVideoEncoder();
        Assert.Throws<InvalidOperationException>(() => encoder.RequestKeyframe());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsNoThrow()
    {
        FakeVideoEncoder encoder = new FakeVideoEncoder();
        await encoder.DisposeAsync();
        await encoder.DisposeAsync();
    }
}
