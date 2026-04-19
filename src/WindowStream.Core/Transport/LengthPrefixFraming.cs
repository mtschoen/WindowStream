using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Transport;

public static class LengthPrefixFraming
{
    public const int LengthPrefixByteLength = 4;

    /// <summary>Sixteen mebibytes — far larger than any JSON control message will ever be.</summary>
    public const int MaximumPayloadByteLength = 16 * 1024 * 1024;

    public static byte[] Encode(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        ValidatePayloadLength(payload.Length);
        byte[] framed = new byte[LengthPrefixByteLength + payload.Length];
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
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        cancellationToken.ThrowIfCancellationRequested();

        byte[] lengthBuffer = new byte[LengthPrefixByteLength];
        await ReadExactlyAsync(stream, lengthBuffer, 0, LengthPrefixByteLength, cancellationToken).ConfigureAwait(false);

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (payloadLength > (uint)MaximumPayloadByteLength)
        {
            throw new FrameTooLargeException((int)Math.Min(payloadLength, int.MaxValue), MaximumPayloadByteLength);
        }
        byte[] payload = new byte[payloadLength];
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
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }
        byte[] framed = Encode(payload);
        await stream.WriteAsync(framed.AsMemory(0, framed.Length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int readThisCall = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);
            if (readThisCall == 0)
            {
                throw new EndOfStreamException(
                    $"stream ended after {totalRead} of {count} bytes");
            }
            totalRead += readThisCall;
        }
    }
}
