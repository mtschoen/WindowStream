using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public static class WorkerChunkPipe
{
    public static async Task WriteChunkAsync(Stream stream, WorkerChunkFrame frame, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4 + 8 + 1];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)frame.Payload.Length));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4, 8), frame.PresentationTimestampMicroseconds);
        header[12] = (byte)(frame.IsKeyframe ? 0x01 : 0x00);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (frame.Payload.Length > 0)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<WorkerChunkFrame> ReadChunkAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4 + 8 + 1];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        ulong presentationTimestampMicroseconds = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4, 8));
        bool isKeyframe = (header[12] & 0x01) != 0;
        byte[] payload = new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
        return new WorkerChunkFrame(presentationTimestampMicroseconds, isKeyframe, payload);
    }

    public static async Task WriteCommandAsync(Stream stream, WorkerCommandFrame command, CancellationToken cancellationToken)
    {
        byte[] tag = new byte[] { (byte)command.Tag };
        await stream.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WorkerCommandFrame> ReadCommandAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return new WorkerCommandFrame((WorkerCommandTag)buffer[0]);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"pipe closed after {total} of {buffer.Length} bytes");
            }
            total += read;
        }
    }
}
