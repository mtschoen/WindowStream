#if WINDOWS
using Silk.NET.Direct3D11;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Integration.Tests.Infrastructure;
using WindowStream.Integration.Tests.Support;
using Xunit;

namespace WindowStream.Integration.Tests.Encode;

/// <summary>
/// Verifies that <see cref="FFmpegNvencEncoder"/> can initialise the NVENC codec, accept a
/// single synthetic NV12 D3D11 texture frame, and emit at least one <see cref="EncodedChunk"/>
/// containing non-empty payload bytes.
///
/// Expected: PASS on a machine with an NVIDIA GPU and driver that exposes h264_nvenc.
/// The <see cref="NvidiaDriverFactAttribute"/> skip-gate keeps the run green on machines
/// without NVIDIA hardware.
/// </summary>
public sealed class NvencInitSmokeTests
{
    [NvidiaDriverFact]
    [Trait("Category", "Integration")]
    public async Task Configures_And_Encodes_A_Single_Synthetic_Texture_Frame()
    {
        using var deviceManager = new Direct3D11DeviceManager();
        await using var encoder = new FFmpegNvencEncoder();
        var options = new EncoderOptions(
            widthPixels: 640,
            heightPixels: 360,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);
        encoder.Configure(options, deviceManager);

        var patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, options.WidthPixels, options.HeightPixels);
        try
        {
            encoder.RequestKeyframe();
            for (var frameIndex = 0; frameIndex < 5; frameIndex++)
            {
                encoder.AcquireFrameTexture(out var poolTexturePointer, out var poolSubresourceIndex);
                unsafe
                {
                    var context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
                    context->CopySubresourceRegion(
                        (ID3D11Resource*)poolTexturePointer,
                        (uint)poolSubresourceIndex,
                        0u, 0u, 0u,
                        (ID3D11Resource*)patternTexturePointer,
                        0u,
                        null);
                }
                var textureFrame = CapturedFrame.FromTexture(
                    widthPixels: options.WidthPixels,
                    heightPixels: options.HeightPixels,
                    rowStrideBytes: options.WidthPixels,
                    pixelFormat: PixelFormat.Nv12,
                    presentationTimestampMicroseconds: frameIndex * 33_333,
                    nativeTexturePointer: poolTexturePointer,
                    textureArrayIndex: poolSubresourceIndex);
                await encoder.EncodeAsync(textureFrame, CancellationToken.None).ConfigureAwait(false);
            }

            EncodedChunk? firstChunk = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var chunk in encoder.EncodedChunks.WithCancellation(timeout.Token).ConfigureAwait(false))
            {
                firstChunk = chunk;
                break;
            }

            Assert.NotNull(firstChunk);
            Assert.True(firstChunk.Payload.Length > 0, "encoded chunk payload must be non-empty");
        }
        finally
        {
            unsafe
            {
                var patternTexture = (ID3D11Texture2D*)patternTexturePointer;
                patternTexture->Release();
            }
        }
    }
}
#endif
