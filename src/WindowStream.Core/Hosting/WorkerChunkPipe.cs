using System.Buffers.Binary;
using System.Text;

namespace WindowStream.Core.Hosting;

public static class WorkerChunkPipe
{
    const byte ChunkTag = 0x00;
    const byte StatusTag = 0x01;

    public static async Task WriteChunkAsync(Stream stream, WorkerChunkFrame frame, CancellationToken cancellationToken)
    {
        var header = new byte[1 + 4 + 8 + 1];
        header[0] = ChunkTag;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), checked((uint)frame.Payload.Length));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(5, 8), frame.PresentationTimestampMicroseconds);
        header[13] = (byte)(frame.IsKeyframe ? 0x01 : 0x00);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (frame.Payload.Length > 0)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task WriteStatusAsync(Stream stream, WorkerStatusFrame frame, CancellationToken cancellationToken)
    {
        var messageBytes = Encoding.UTF8.GetBytes(frame.Message);
        var header = new byte[1 + 1 + 1 + 4 + 4];
        header[0] = StatusTag;
        header[1] = (byte)frame.Kind;
        header[2] = (byte)frame.Cause;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(3, 4), frame.LastFrameAgeMilliseconds);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(7, 4), checked((uint)messageBytes.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (messageBytes.Length > 0)
        {
            await stream.WriteAsync(messageBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<WorkerPipeFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var tag = new byte[1];
        await ReadExactlyAsync(stream, tag, cancellationToken).ConfigureAwait(false);
        return tag[0] switch
        {
            ChunkTag => new WorkerPipeFrame.ChunkPayload(await ReadChunkBodyAsync(stream, cancellationToken).ConfigureAwait(false)),
            StatusTag => new WorkerPipeFrame.StatusPayload(await ReadStatusBodyAsync(stream, cancellationToken).ConfigureAwait(false)),
            _ => throw new InvalidDataException($"unknown worker pipe frame tag: 0x{tag[0]:X2}")
        };
    }

    static async Task<WorkerChunkFrame> ReadChunkBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4 + 8 + 1];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        var presentationTimestampMicroseconds = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4, 8));
        var isKeyframe = (header[12] & 0x01) != 0;
        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
        return new WorkerChunkFrame(presentationTimestampMicroseconds, isKeyframe, payload);
    }

    static async Task<WorkerStatusFrame> ReadStatusBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[1 + 1 + 4 + 4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var kind = (WorkerStatusKind)header[0];
        var cause = (Capture.Detection.StallCause)header[1];
        var lastFrameAge = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(2, 4));
        var messageLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6, 4));
        var message = string.Empty;
        if (messageLength > 0)
        {
            var messageBytes = new byte[messageLength];
            await ReadExactlyAsync(stream, messageBytes, cancellationToken).ConfigureAwait(false);
            message = Encoding.UTF8.GetString(messageBytes);
        }
        return new WorkerStatusFrame(kind, cause, lastFrameAge, message);
    }

    public static async Task WriteCommandAsync(Stream stream, WorkerCommandFrame command, CancellationToken cancellationToken)
    {
        var tag = new[] { (byte)command.Tag };
        await stream.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WorkerCommandFrame> ReadCommandAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return new WorkerCommandFrame((WorkerCommandTag)buffer[0]);
    }

    static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"pipe closed after {total} of {buffer.Length} bytes");
            }
            total += read;
        }
    }
}
