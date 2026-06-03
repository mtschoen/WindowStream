using System.Buffers.Binary;

namespace WindowStream.Core.Transport;

public static class LengthPrefixFraming
{
    public const int LengthPrefixByteLength = 4;

    /// <summary>Sixteen mebibytes — far larger than any JSON control message will ever be.</summary>
    public const int MaximumPayloadByteLength = 16 * 1024 * 1024;

    public static byte[] Encode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayloadLength(payload.Length);
        var framed = new byte[LengthPrefixByteLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(0, LengthPrefixByteLength), (uint)payload.Length);
        Array.Copy(payload, 0, framed, LengthPrefixByteLength, payload.Length);
        return framed;
    }

    public static void ValidatePayloadLength(int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "length must be non-negative");
        }
        if (length > MaximumPayloadByteLength)
        {
            throw new FrameTooLargeException(length, MaximumPayloadByteLength);
        }
    }

    public static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var lengthBuffer = new byte[LengthPrefixByteLength];
        await ReadExactlyAsync(stream, lengthBuffer, 0, LengthPrefixByteLength, cancellationToken).ConfigureAwait(false);

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (payloadLength > MaximumPayloadByteLength)
        {
            throw new FrameTooLargeException((int)Math.Min(payloadLength, int.MaxValue), MaximumPayloadByteLength);
        }
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            await ReadExactlyAsync(stream, payload, 0, (int)payloadLength, cancellationToken).ConfigureAwait(false);
        }
        return payload;
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var framed = Encode(payload);
        await stream.WriteAsync(framed.AsMemory(0, framed.Length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var readThisCall = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);
            if (readThisCall == 0)
            {
                throw new EndOfStreamException(
                    $"stream ended after {totalRead} of {count} bytes");
            }
            totalRead += readThisCall;
        }
    }
}
