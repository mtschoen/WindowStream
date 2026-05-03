using System;

namespace WindowStream.Core.Capture;

public enum FrameRepresentation
{
    Bytes,
    Texture,
}

public sealed class CapturedFrame
{
    public int widthPixels { get; }
    public int heightPixels { get; }
    public int rowStrideBytes { get; }
    public PixelFormat pixelFormat { get; }
    public long presentationTimestampMicroseconds { get; }
    public FrameRepresentation representation { get; }
    public ReadOnlyMemory<byte> pixelBuffer { get; }
    public nint nativeTexturePointer { get; }
    public int textureArrayIndex { get; }

    /// <summary>
    /// Construct a managed-byte (CPU-resident) <see cref="CapturedFrame"/>.
    /// During the GPU-resident pipeline transition this is the only path
    /// production code uses; M5 scopes this constructor to internal once
    /// production code constructs texture frames exclusively.
    /// </summary>
    public CapturedFrame(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        ReadOnlyMemory<byte> pixelBuffer)
    {
        ValidateCommon(widthPixels, heightPixels, rowStrideBytes, pixelFormat, presentationTimestampMicroseconds);

        long expectedLength = pixelFormat == PixelFormat.Nv12
            ? (long)rowStrideBytes * heightPixels * 3 / 2
            : (long)rowStrideBytes * heightPixels;
        if (pixelBuffer.Length < expectedLength)
        {
            throw new ArgumentException(
                "pixelBuffer is smaller than widthPixels * heightPixels for the declared stride and format.",
                nameof(pixelBuffer));
        }

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.rowStrideBytes = rowStrideBytes;
        this.pixelFormat = pixelFormat;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
        this.representation = FrameRepresentation.Bytes;
        this.pixelBuffer = pixelBuffer;
        this.nativeTexturePointer = 0;
        this.textureArrayIndex = 0;
    }

    private CapturedFrame(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        nint nativeTexturePointer,
        int textureArrayIndex)
    {
        ValidateCommon(widthPixels, heightPixels, rowStrideBytes, pixelFormat, presentationTimestampMicroseconds);

        if (nativeTexturePointer == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeTexturePointer), "Texture pointer must be non-zero.");
        }
        if (textureArrayIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(textureArrayIndex), "Texture array index must be non-negative.");
        }

        this.widthPixels = widthPixels;
        this.heightPixels = heightPixels;
        this.rowStrideBytes = rowStrideBytes;
        this.pixelFormat = pixelFormat;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
        this.representation = FrameRepresentation.Texture;
        this.pixelBuffer = ReadOnlyMemory<byte>.Empty;
        this.nativeTexturePointer = nativeTexturePointer;
        this.textureArrayIndex = textureArrayIndex;
    }

    /// <summary>
    /// Construct a managed-byte (CPU-resident) <see cref="CapturedFrame"/>.
    /// Equivalent to the public constructor; provided as the documented
    /// factory entry point for the GPU-resident pipeline transition (paired
    /// with <see cref="FromTexture"/>).
    /// </summary>
    public static CapturedFrame FromBytes(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        ReadOnlyMemory<byte> pixelBuffer) =>
        new CapturedFrame(
            widthPixels,
            heightPixels,
            rowStrideBytes,
            pixelFormat,
            presentationTimestampMicroseconds,
            pixelBuffer);

    /// <summary>
    /// Construct a native-texture (GPU-resident) <see cref="CapturedFrame"/>.
    /// <paramref name="nativeTexturePointer"/> is an <c>ID3D11Texture2D*</c>
    /// owned by the producer; the consumer is responsible for honouring the
    /// producer's release contract (the encoder's <c>hw_frames_ctx</c> pool
    /// in the post-M4 pipeline). <paramref name="textureArrayIndex"/> is the
    /// subresource index within the texture array (0 for non-array textures).
    /// </summary>
    public static CapturedFrame FromTexture(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        nint nativeTexturePointer,
        int textureArrayIndex) =>
        new CapturedFrame(
            widthPixels,
            heightPixels,
            rowStrideBytes,
            pixelFormat,
            presentationTimestampMicroseconds,
            nativeTexturePointer,
            textureArrayIndex);

    private static void ValidateCommon(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds)
    {
        if (widthPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPixels));
        }
        if (heightPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightPixels));
        }

        int minimumStride = pixelFormat switch
        {
            PixelFormat.Bgra32 => widthPixels * 4,
            PixelFormat.Nv12 => widthPixels,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        };
        if (rowStrideBytes < minimumStride)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStrideBytes));
        }
        if (presentationTimestampMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationTimestampMicroseconds));
        }
    }
}
