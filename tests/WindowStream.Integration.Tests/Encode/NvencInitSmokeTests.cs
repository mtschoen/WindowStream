#if WINDOWS
using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Encode;
using WindowStream.Integration.Tests.Infrastructure;
using WindowStream.Integration.Tests.Support;
using Xunit;

namespace WindowStream.Integration.Tests.Encode;

/// <summary>
/// Verifies that <see cref="FFmpegNvencEncoder"/> can initialise the NVENC codec, accept a
/// single synthetic frame, and emit at least one <see cref="EncodedChunk"/> containing
/// non-empty payload bytes.
///
/// Expected: PASS on a machine with an NVIDIA GPU and driver that exposes h264_nvenc.
/// The <see cref="NvidiaDriverFactAttribute"/> skip-gate keeps the run green on machines
/// without NVIDIA hardware.
/// </summary>
public sealed class NvencInitSmokeTests
{
    [NvidiaDriverFact]
    [Trait("Category", "Integration")]
    public async Task Configures_And_Encodes_A_Single_Solid_Color_Frame()
    {
        // Arrange — configure the encoder with realistic 640×360 @ 30 fps settings.
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        EncoderOptions options = new EncoderOptions(
            widthPixels: 640,
            heightPixels: 360,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);
        encoder.Configure(options);

        // Create a synthetic BGRA frame filled with a solid mid-green colour.
        var frame = SolidColorFrameFactory.CreateSolidColorBgra(640, 360, red: 32, green: 128, blue: 64);

        // Request an IDR frame so the encoder is forced to emit output on the first frame.
        encoder.RequestKeyframe();

        // Act — push multiple frames. NVENC look-ahead may buffer several frames before
        // producing output, even with low-latency settings.  Five frames is enough to
        // prime the GPU pipeline on any NVIDIA card.
        for (int frameIndex = 0; frameIndex < 5; frameIndex++)
        {
            await encoder.EncodeAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }

        EncodedChunk? firstChunk = null;
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (EncodedChunk chunk in encoder.EncodedChunks.WithCancellation(timeout.Token).ConfigureAwait(false))
        {
            firstChunk = chunk;
            break;
        }

        // Assert — at least one non-empty chunk was produced.
        Assert.NotNull(firstChunk);
        Assert.True(firstChunk!.payload.Length > 0, "encoded chunk payload must be non-empty");
    }
}
#endif
