using System;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class LengthPrefixFramingEncodeTests
{
    [Fact]
    public void EncodePrependsBigEndianLength()
    {
        byte[] payload = new byte[] { 0x41, 0x42, 0x43 };
        byte[] framed = LengthPrefixFraming.Encode(payload);
        Assert.Equal(7, framed.Length);
        Assert.Equal(0x00, framed[0]);
        Assert.Equal(0x00, framed[1]);
        Assert.Equal(0x00, framed[2]);
        Assert.Equal(0x03, framed[3]);
        Assert.Equal(0x41, framed[4]);
        Assert.Equal(0x42, framed[5]);
        Assert.Equal(0x43, framed[6]);
    }

    [Fact]
    public void EncodeAcceptsEmptyPayload()
    {
        byte[] framed = LengthPrefixFraming.Encode(Array.Empty<byte>());
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, framed);
    }

    [Fact]
    public void EncodeRejectsNullPayload()
    {
        Assert.Throws<ArgumentNullException>(() => LengthPrefixFraming.Encode(null!));
    }

    [Fact]
    public void EncodeRejectsOversizedPayload()
    {
        // We won't actually allocate a 16 MiB buffer; we use the Span overload with a fake length.
        Assert.Throws<FrameTooLargeException>(
            () => LengthPrefixFraming.ValidatePayloadLength(LengthPrefixFraming.MaximumPayloadByteLength + 1));
    }

    [Fact]
    public void EncodeAllowsMaximumExactLength()
    {
        LengthPrefixFraming.ValidatePayloadLength(LengthPrefixFraming.MaximumPayloadByteLength);
    }

    [Fact]
    public void ValidatePayloadLengthRejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LengthPrefixFraming.ValidatePayloadLength(-1));
    }
}
