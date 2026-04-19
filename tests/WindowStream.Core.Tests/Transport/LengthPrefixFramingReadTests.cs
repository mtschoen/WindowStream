using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class LengthPrefixFramingReadTests
{
    [Fact]
    public async Task ReadsCompletePayloadInOneCall()
    {
        byte[] framed = LengthPrefixFraming.Encode(new byte[] { 0xAA, 0xBB, 0xCC });
        using MemoryStream stream = new(framed);
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, payload);
    }

    [Fact]
    public async Task ReassemblesAcrossMultipleReads()
    {
        byte[] framed = LengthPrefixFraming.Encode(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
        using SlowStream stream = new(framed, chunkSize: 1);
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }, payload);
    }

    [Fact]
    public async Task EndOfStreamInsideLengthPrefixThrows()
    {
        using MemoryStream stream = new(new byte[] { 0x00, 0x00 });
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task EndOfStreamInsidePayloadThrows()
    {
        // length = 4, payload delivered as only 2 bytes
        byte[] truncated = new byte[] { 0x00, 0x00, 0x00, 0x04, 0xAA, 0xBB };
        using MemoryStream stream = new(truncated);
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOversizedFrame()
    {
        // length = MaximumPayloadByteLength + 1 encoded big-endian
        uint oversize = (uint)LengthPrefixFraming.MaximumPayloadByteLength + 1;
        byte[] header = new byte[]
        {
            (byte)((oversize >> 24) & 0xFF),
            (byte)((oversize >> 16) & 0xFF),
            (byte)((oversize >> 8) & 0xFF),
            (byte)(oversize & 0xFF)
        };
        using MemoryStream stream = new(header);
        await Assert.ThrowsAsync<FrameTooLargeException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using MemoryStream stream = new();
        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(stream, source.Token));
    }

    [Fact]
    public async Task ReadsEmptyPayload()
    {
        byte[] framed = LengthPrefixFraming.Encode(Array.Empty<byte>());
        using MemoryStream stream = new(framed);
        byte[] payload = await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Empty(payload);
    }

    [Fact]
    public async Task WriteFrameAsyncRoundTrips()
    {
        byte[] payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        using MemoryStream stream = new();
        await LengthPrefixFraming.WriteFrameAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;
        byte[] received = await LengthPrefixFraming.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task WriteFrameAsyncRejectsNullStream()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await LengthPrefixFraming.WriteFrameAsync(null!, new byte[] { 0x01 }, CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrameAsyncRejectsNullStream()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await LengthPrefixFraming.ReadFrameAsync(null!, CancellationToken.None));
    }

    // Helper: a Stream that returns at most `chunkSize` bytes per read call, forcing the
    // reader to loop until the requested length is satisfied.
    private sealed class SlowStream : Stream
    {
        private readonly byte[] data;
        private readonly int chunkSize;
        private int position;

        public SlowStream(byte[] data, int chunkSize)
        {
            this.data = data;
            this.chunkSize = chunkSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int actual = Math.Min(Math.Min(count, chunkSize), data.Length - position);
            Array.Copy(data, position, buffer, offset, actual);
            position += actual;
            return actual;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
