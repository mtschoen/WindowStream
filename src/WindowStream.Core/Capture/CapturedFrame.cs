namespace WindowStream.Core.Capture;

public enum FrameRepresentation
{
    Bytes,
    Texture,
}

public sealed class CapturedFrame
{
    public int WidthPixels { get; }
    public int HeightPixels { get; }
    public int RowStrideBytes { get; }
    public PixelFormat PixelFormat { get; }
    public long PresentationTimestampMicroseconds { get; }
    public FrameRepresentation Representation { get; }
    public ReadOnlyMemory<byte> PixelBuffer { get; }
    public nint NativeTexturePointer { get; }
    public int TextureArrayIndex { get; }

    /// <summary>
    /// Construct a managed-byte (CPU-resident) <see cref="CapturedFrame"/>.
    /// Test-only entry point post-M4 — production code (capture, encode,
    /// hosting) only constructs texture-bearing frames. Visible to
    /// <c>WindowStream.Core.Tests</c> and <c>WindowStream.Integration.Tests</c>
    /// via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal CapturedFrame(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds,
        ReadOnlyMemory<byte> pixelBuffer)
    {
        ValidateCommon(widthPixels, heightPixels, rowStrideBytes, pixelFormat, presentationTimestampMicroseconds);

        var expectedLength = pixelFormat == PixelFormat.Nv12
            ? (long)rowStrideBytes * heightPixels * 3 / 2
            : (long)rowStrideBytes * heightPixels;
        if (pixelBuffer.Length < expectedLength)
        {
            throw new ArgumentException(
                "pixelBuffer is smaller than widthPixels * heightPixels for the declared stride and format.",
                nameof(pixelBuffer));
        }

        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        RowStrideBytes = rowStrideBytes;
        PixelFormat = pixelFormat;
        PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
        Representation = FrameRepresentation.Bytes;
        PixelBuffer = pixelBuffer;
        NativeTexturePointer = 0;
        TextureArrayIndex = 0;
    }

    CapturedFrame(
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

        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        RowStrideBytes = rowStrideBytes;
        PixelFormat = pixelFormat;
        PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
        Representation = FrameRepresentation.Texture;
        PixelBuffer = ReadOnlyMemory<byte>.Empty;
        NativeTexturePointer = nativeTexturePointer;
        TextureArrayIndex = textureArrayIndex;
    }

    /// <summary>
    /// Construct a managed-byte (CPU-resident) <see cref="CapturedFrame"/>.
    /// Test-only — same visibility as the bytes constructor.
    /// </summary>
    internal static CapturedFrame FromBytes(
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

    static void ValidateCommon(
        int widthPixels,
        int heightPixels,
        int rowStrideBytes,
        PixelFormat pixelFormat,
        long presentationTimestampMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);

        var minimumStride = pixelFormat switch
        {
            PixelFormat.Bgra32 => widthPixels * 4,
            PixelFormat.Nv12 => widthPixels,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat)),
        };
        ArgumentOutOfRangeException.ThrowIfLessThan(rowStrideBytes, minimumStride);
        ArgumentOutOfRangeException.ThrowIfNegative(presentationTimestampMicroseconds);
    }
}
