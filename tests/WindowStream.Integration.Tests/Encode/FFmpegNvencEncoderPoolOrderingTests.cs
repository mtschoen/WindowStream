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

public sealed class FFmpegNvencEncoderPoolOrderingTests
{
    private const int WidthPixels = 640;
    private const int HeightPixels = 360;

    /// <summary>
    /// Acquires two pool frames, then encodes them in the OPPOSITE order
    /// from acquisition. The pre-fix FIFO assertion at
    /// FFmpegNvencEncoder.cs:305 trips because TryDequeue returns A's
    /// AVFrame but the CapturedFrame's (texP, idx) belongs to B.
    /// Post-fix the dictionary lookup finds the correct AVFrame for each.
    /// </summary>
    [NvidiaDriverFact]
    [Trait("Category", "Integration")]
    public async Task OutOfOrderEncode_Succeeds()
    {
        using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        encoder.Configure(
            new EncoderOptions(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                framesPerSecond: 30,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 2),
            deviceManager);

        nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, WidthPixels, HeightPixels);
        try
        {
            // Acquire two distinct pool textures.
            encoder.AcquireFrameTexture(out nint texturePointerA, out int subresourceIndexA);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerA, subresourceIndexA);
            CapturedFrame frameA = CapturedFrame.FromTexture(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                rowStrideBytes: WidthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                nativeTexturePointer: texturePointerA,
                textureArrayIndex: subresourceIndexA);

            encoder.AcquireFrameTexture(out nint texturePointerB, out int subresourceIndexB);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerB, subresourceIndexB);
            CapturedFrame frameB = CapturedFrame.FromTexture(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                rowStrideBytes: WidthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 33_333,
                nativeTexturePointer: texturePointerB,
                textureArrayIndex: subresourceIndexB);

            encoder.RequestKeyframe();

            // Encode B first, then A — opposite of acquisition order.
            await encoder.EncodeAsync(frameB, CancellationToken.None).ConfigureAwait(false);
            await encoder.EncodeAsync(frameA, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Acquires a pool frame, releases it without encoding (simulating the
    /// worker pause-skip path), acquires another, and encodes successfully.
    /// Verifies that ReleaseFrameTexture returns the AVFrame to the pool
    /// cleanly and the subsequent EncodeAsync finds its own matching frame.
    /// </summary>
    [NvidiaDriverFact]
    [Trait("Category", "Integration")]
    public async Task AcquireReleaseAcquireEncode_Succeeds()
    {
        using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        encoder.Configure(
            new EncoderOptions(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                framesPerSecond: 30,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 2),
            deviceManager);

        nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, WidthPixels, HeightPixels);
        try
        {
            // Acquire frame A and immediately release it (simulating pause-skip).
            encoder.AcquireFrameTexture(out nint texturePointerA, out int subresourceIndexA);
            encoder.ReleaseFrameTexture(texturePointerA, subresourceIndexA);

            // Acquire frame B and encode normally.
            encoder.AcquireFrameTexture(out nint texturePointerB, out int subresourceIndexB);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerB, subresourceIndexB);
            CapturedFrame frameB = CapturedFrame.FromTexture(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                rowStrideBytes: WidthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                nativeTexturePointer: texturePointerB,
                textureArrayIndex: subresourceIndexB);

            encoder.RequestKeyframe();
            await encoder.EncodeAsync(frameB, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Regression test for Gitea #6. Spawns three concurrent FFmpegNvencEncoder
    /// instances pumping synthetic captured frames in parallel on the same GPU,
    /// each encoding for ~30s at 30fps (~900 frames per encoder). Pre-fix this
    /// would surface the FIFO assertion within ~10s on multi-worker contention.
    /// Post-fix all three must survive without EncoderException.
    /// </summary>
    [NvidiaDriverFact]
    [Trait("Category", "Integration")]
    [Trait("Category", "LongRunning")]
    public async Task ThreeConcurrentEncoders_SurviveThirtySeconds()
    {
        const int DurationSeconds = 30;
        const int FramesPerSecond = 30;
        const int TotalFrames = DurationSeconds * FramesPerSecond;

        using CancellationTokenSource overallTimeout =
            new CancellationTokenSource(System.TimeSpan.FromSeconds(DurationSeconds + 10));

        Task[] encoderTasks = new Task[3];
        for (int encoderIndex = 0; encoderIndex < 3; encoderIndex++)
        {
            int capturedEncoderIndex = encoderIndex;
            encoderTasks[encoderIndex] = Task.Run(
                () => RunEncoderForFrames(TotalFrames, FramesPerSecond, capturedEncoderIndex, overallTimeout.Token),
                overallTimeout.Token);
        }

        await Task.WhenAll(encoderTasks).ConfigureAwait(false);
    }

    private static async Task RunEncoderForFrames(
        int totalFrames,
        int framesPerSecond,
        int encoderIndex,
        CancellationToken cancellationToken)
    {
        using Direct3D11DeviceManager deviceManager = new Direct3D11DeviceManager();
        await using FFmpegNvencEncoder encoder = new FFmpegNvencEncoder();
        encoder.Configure(
            new EncoderOptions(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                framesPerSecond: framesPerSecond,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 2),
            deviceManager);

        nint patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, WidthPixels, HeightPixels);
        try
        {
            long frameDurationMicroseconds = 1_000_000L / framesPerSecond;
            encoder.RequestKeyframe();
            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                encoder.AcquireFrameTexture(out nint poolTexturePointer, out int poolSubresourceIndex);
                CopyPatternInto(deviceManager, patternTexturePointer, poolTexturePointer, poolSubresourceIndex);
                CapturedFrame textureFrame = CapturedFrame.FromTexture(
                    widthPixels: WidthPixels,
                    heightPixels: HeightPixels,
                    rowStrideBytes: WidthPixels,
                    pixelFormat: PixelFormat.Nv12,
                    presentationTimestampMicroseconds: frameIndex * frameDurationMicroseconds,
                    nativeTexturePointer: poolTexturePointer,
                    textureArrayIndex: poolSubresourceIndex);
                await encoder.EncodeAsync(textureFrame, cancellationToken).ConfigureAwait(false);
            }
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

    private static void CopyPatternInto(
        Direct3D11DeviceManager deviceManager,
        nint patternTexturePointer,
        nint destinationTexturePointer,
        int destinationSubresourceIndex)
    {
        unsafe
        {
            ID3D11DeviceContext* context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
            context->CopySubresourceRegion(
                (ID3D11Resource*)destinationTexturePointer,
                (uint)destinationSubresourceIndex,
                0u, 0u, 0u,
                (ID3D11Resource*)patternTexturePointer,
                0u,
                (Box*)null);
        }
    }
}

#endif
