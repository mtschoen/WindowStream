using System;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class StreamStoppedReasonTests
{
    [Theory]
    [InlineData(StreamStoppedReason.ClosedByViewer, "CLOSED_BY_VIEWER")]
    [InlineData(StreamStoppedReason.WindowGone, "WINDOW_GONE")]
    [InlineData(StreamStoppedReason.EncoderFailed, "ENCODER_FAILED")]
    [InlineData(StreamStoppedReason.CaptureFailed, "CAPTURE_FAILED")]
    [InlineData(StreamStoppedReason.StreamHung, "STREAM_HUNG")]
    [InlineData(StreamStoppedReason.ServerShutdown, "SERVER_SHUTDOWN")]
    public void WireNameRoundTripsThroughParse(StreamStoppedReason reason, string wireName)
    {
        Assert.Equal(wireName, StreamStoppedReasonNames.ToWireName(reason));
        Assert.Equal(reason, StreamStoppedReasonNames.Parse(wireName));
    }

    [Fact]
    public void ParseThrowsForUnknownValue()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => StreamStoppedReasonNames.Parse("NOT_A_REAL_REASON"));
        Assert.Contains("NOT_A_REAL_REASON", exception.Message);
    }

    [Fact]
    public void ToWireNameThrowsForUndefinedEnumValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamStoppedReasonNames.ToWireName((StreamStoppedReason)9999));
    }
}
