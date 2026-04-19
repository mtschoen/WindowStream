using System;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class ProtocolErrorCodeTests
{
    [Theory]
    [InlineData(ProtocolErrorCode.VersionMismatch, "VERSION_MISMATCH")]
    [InlineData(ProtocolErrorCode.ViewerBusy, "VIEWER_BUSY")]
    [InlineData(ProtocolErrorCode.WindowGone, "WINDOW_GONE")]
    [InlineData(ProtocolErrorCode.CaptureFailed, "CAPTURE_FAILED")]
    [InlineData(ProtocolErrorCode.EncodeFailed, "ENCODE_FAILED")]
    [InlineData(ProtocolErrorCode.MalformedMessage, "MALFORMED_MESSAGE")]
    public void WireNameRoundTripsThroughParse(ProtocolErrorCode code, string wireName)
    {
        Assert.Equal(wireName, ProtocolErrorCodeNames.ToWireName(code));
        Assert.Equal(code, ProtocolErrorCodeNames.Parse(wireName));
    }

    [Fact]
    public void ParseThrowsForUnknownValue()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => ProtocolErrorCodeNames.Parse("NOT_A_REAL_CODE"));
        Assert.Contains("NOT_A_REAL_CODE", exception.Message);
    }

    [Fact]
    public void ToWireNameThrowsForUndefinedEnumValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProtocolErrorCodeNames.ToWireName((ProtocolErrorCode)9999));
    }
}
