#if WINDOWS
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Direct3D11;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Integration.Tests.Infrastructure;
using WindowStream.Integration.Tests.Support;
using Xunit;

namespace WindowStream.Integration.Tests.Encode;

public sealed class FFmpegNvencEncoderHwaccelTests
{
    [NvidiaDriverTheory]
    [InlineData(640, 360)]   // 100% baseline
    [InlineData(800, 450)]   // 125%
    [InlineData(960, 540)]   // 150%
    [InlineData(1120, 630)]  // 175%
    [Trait("Category", "Integration")]
    public async Task EncodesTextureFrame_ProducesNonEmptyChunk(int widthPixels, int heightPixels)
    {
        using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        EncoderOptions encoderOptions = new EncoderOptions(
            widthPixels: widthPixels,
            heightPixels: heightPixels,
            framesPerSecond: 30,
            bitrateBitsPerSecond: 4_000_000,
            groupOfPicturesLength: 30,
            safetyKeyframeIntervalSeconds: 2);
        encoder.Configure(encoderOptions, deviceManager);

        // Acquire a pool frame texture, copy our synthetic pattern into it.
        encoder.AcquireFrameTexture(out nint poolTexturePointer, out int poolSubresourceIndex);
        nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, widthPixels, heightPixels);

        unsafe
        {
            ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
            // Copy the pattern texture into the pool's NV12 texture (subresource index `poolSubresourceIndex`).
            context->CopySubresourceRegion(
                (ID3D11Resource*)poolTexturePointer,
                (uint)poolSubresourceIndex,
                0u, 0u, 0u,
                (ID3D11Resource*)patternTexturePointer,
                0u,
                (Box*)null);
        }
        try
        {
            CapturedFrame textureFrame = CapturedFrame.FromTexture(
                widthPixels: widthPixels,
                heightPixels: heightPixels,
                rowStrideBytes: widthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                nativeTexturePointer: poolTexturePointer,
                textureArrayIndex: poolSubresourceIndex);

            encoder.RequestKeyframe();

            // Push 5 frames to prime NVENC; the pattern is the same each time but the
            // pts varies, which is what the encoder cares about.
            for (int frameIndex = 0; frameIndex < 5; frameIndex++)
            {
                if (frameIndex > 0)
                {
                    encoder.AcquireFrameTexture(out poolTexturePointer, out poolSubresourceIndex);
                    unsafe
                    {
                        ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
                        context->CopySubresourceRegion(
                            (ID3D11Resource*)poolTexturePointer,
                            (uint)poolSubresourceIndex,
                            0u, 0u, 0u,
                            (ID3D11Resource*)patternTexturePointer,
                            0u,
                            (Box*)null);
                    }
                    textureFrame = CapturedFrame.FromTexture(
                        widthPixels: widthPixels,
                        heightPixels: heightPixels,
                        rowStrideBytes: widthPixels,
                        pixelFormat: PixelFormat.Nv12,
                        presentationTimestampMicroseconds: frameIndex * 33_333,
                        nativeTexturePointer: poolTexturePointer,
                        textureArrayIndex: poolSubresourceIndex);
                }
                await encoder.EncodeAsync(textureFrame, CancellationToken.None).ConfigureAwait(false);
            }

            EncodedChunk? firstChunk = null;
            using CancellationTokenSource timeout = new CancellationTokenSource(System.TimeSpan.FromSeconds(5));
            await foreach (EncodedChunk chunk in encoder.EncodedChunks.WithCancellation(timeout.Token).ConfigureAwait(false))
            {
                firstChunk = chunk;
                break;
            }

            Assert.NotNull(firstChunk);
            Assert.True(firstChunk!.payload.Length > 0,
                $"encoded chunk payload must be non-empty at {widthPixels}x{heightPixels}");
        }
        finally
        {
            unsafe
            {
                ID3D11Texture2D* patternTexture = (ID3D11Texture2D*)patternTexturePointer;
                patternTexture->Release();
            }
        }
    }
}

#endif
