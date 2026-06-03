#if WINDOWS
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
    const int WidthPixels = 640;
    const int HeightPixels = 360;

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
        using var deviceManager = new Direct3D11DeviceManager();
        await using var encoder = new FFmpegNvencEncoder();
        encoder.Configure(
            new EncoderOptions(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                framesPerSecond: 30,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 2),
            deviceManager);

        var patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, WidthPixels, HeightPixels);
        try
        {
            // Acquire two distinct pool textures.
            encoder.AcquireFrameTexture(out var texturePointerA, out var subresourceIndexA);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerA, subresourceIndexA);
            var frameA = CapturedFrame.FromTexture(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                rowStrideBytes: WidthPixels,
                pixelFormat: PixelFormat.Nv12,
                presentationTimestampMicroseconds: 0,
                nativeTexturePointer: texturePointerA,
                textureArrayIndex: subresourceIndexA);

            encoder.AcquireFrameTexture(out var texturePointerB, out var subresourceIndexB);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerB, subresourceIndexB);
            var frameB = CapturedFrame.FromTexture(
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
                var patternTexture = (ID3D11Texture2D*)patternTexturePointer;
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
        using var deviceManager = new Direct3D11DeviceManager();
        await using var encoder = new FFmpegNvencEncoder();
        encoder.Configure(
            new EncoderOptions(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                framesPerSecond: 30,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 2),
            deviceManager);

        var patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, WidthPixels, HeightPixels);
        try
        {
            // Acquire frame A and immediately release it (simulating pause-skip).
            encoder.AcquireFrameTexture(out var texturePointerA, out var subresourceIndexA);
            encoder.ReleaseFrameTexture(texturePointerA, subresourceIndexA);

            // Acquire frame B and encode normally.
            encoder.AcquireFrameTexture(out var texturePointerB, out var subresourceIndexB);
            CopyPatternInto(deviceManager, patternTexturePointer, texturePointerB, subresourceIndexB);
            var frameB = CapturedFrame.FromTexture(
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
                var patternTexture = (ID3D11Texture2D*)patternTexturePointer;
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
        const int durationSeconds = 30;
        const int framesPerSecond = 30;
        const int totalFrames = durationSeconds * framesPerSecond;

        using var overallTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 10));

        var encoderTasks = new Task[3];
        for (var encoderIndex = 0; encoderIndex < 3; encoderIndex++)
        {
            // overallTimeout is awaited (Task.WhenAll) before its using disposes.
            // ReSharper disable AccessToDisposedClosure
            encoderTasks[encoderIndex] = Task.Run(
                () => RunEncoderForFrames(totalFrames, framesPerSecond, overallTimeout.Token),
                overallTimeout.Token);
            // ReSharper restore AccessToDisposedClosure
        }

        await Task.WhenAll(encoderTasks).ConfigureAwait(false);
    }

    static async Task RunEncoderForFrames(
        int totalFrames,
        int framesPerSecond,
        CancellationToken cancellationToken)
    {
        using var deviceManager = new Direct3D11DeviceManager();
        await using var encoder = new FFmpegNvencEncoder();
        encoder.Configure(
            new EncoderOptions(
                widthPixels: WidthPixels,
                heightPixels: HeightPixels,
                framesPerSecond: framesPerSecond,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 2),
            deviceManager);

        var patternTexturePointer = Nv12TextureFactory.CreateQuadrantPatternTexture(
            deviceManager, WidthPixels, HeightPixels);
        try
        {
            var frameDurationMicroseconds = 1_000_000L / framesPerSecond;
            encoder.RequestKeyframe();
            for (var frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                encoder.AcquireFrameTexture(out var poolTexturePointer, out var poolSubresourceIndex);
                CopyPatternInto(deviceManager, patternTexturePointer, poolTexturePointer, poolSubresourceIndex);
                var textureFrame = CapturedFrame.FromTexture(
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
                var patternTexture = (ID3D11Texture2D*)patternTexturePointer;
                patternTexture->Release();
            }
        }
    }

    static void CopyPatternInto(
        Direct3D11DeviceManager deviceManager,
        nint patternTexturePointer,
        nint destinationTexturePointer,
        int destinationSubresourceIndex)
    {
        unsafe
        {
            var context = (ID3D11DeviceContext*)deviceManager.NativeContextPointer;
            context->CopySubresourceRegion(
                (ID3D11Resource*)destinationTexturePointer,
                (uint)destinationSubresourceIndex,
                0u, 0u, 0u,
                (ID3D11Resource*)patternTexturePointer,
                0u,
                null);
        }
    }
}

#endif
