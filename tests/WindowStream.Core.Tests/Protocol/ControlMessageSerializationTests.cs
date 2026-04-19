using System.Collections.Generic;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class ControlMessageSerializationTests
{
    [Fact]
    public void HelloRoundTrips()
    {
        ControlMessage original = new HelloMessage(
            ViewerVersion: 1,
            DisplayCapabilities: new DisplayCapabilities(3840, 2160, new[] { "h264" }));
        AssertRoundTrip(original);
    }

    [Fact]
    public void ServerHelloWithActiveStreamRoundTrips()
    {
        ControlMessage original = new ServerHelloMessage(
            ServerVersion: 1,
            ActiveStream: new ActiveStreamInformation(7, 51001, "h264", 2560, 1440, 120));
        AssertRoundTrip(original);
    }

    [Fact]
    public void ServerHelloWithNullActiveStreamRoundTrips()
    {
        ControlMessage original = new ServerHelloMessage(ServerVersion: 1, ActiveStream: null);
        AssertRoundTrip(original);
    }

    [Fact]
    public void StreamStartedRoundTrips()
    {
        ControlMessage original = new StreamStartedMessage(7, 51001, "h264", 2560, 1440, 120);
        AssertRoundTrip(original);
    }

    [Fact]
    public void StreamStoppedRoundTrips()
    {
        AssertRoundTrip(new StreamStoppedMessage(7));
    }

    [Fact]
    public void RequestKeyframeRoundTrips()
    {
        AssertRoundTrip(new RequestKeyframeMessage(7));
    }

    [Fact]
    public void HeartbeatRoundTrips()
    {
        AssertRoundTrip(HeartbeatMessage.Instance);
    }

    [Fact]
    public void ErrorRoundTrips()
    {
        AssertRoundTrip(new ErrorMessage(ProtocolErrorCode.ViewerBusy, "already connected"));
    }

    [Fact]
    public void HeartbeatEmitsExactlyTypeField()
    {
        string encoded = ControlMessageSerialization.Serialize(HeartbeatMessage.Instance);
        Assert.Equal("{\"type\":\"HEARTBEAT\"}", encoded);
    }

    [Fact]
    public void UnknownTypeThrowsMalformed()
    {
        MalformedMessageException exception = Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("{\"type\":\"WAT\"}"));
        Assert.Contains("WAT", exception.Message);
    }

    [Fact]
    public void MissingTypeThrowsMalformed()
    {
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("{}"));
    }

    [Fact]
    public void BrokenJsonThrowsMalformed()
    {
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("not json"));
    }

    [Fact]
    public void NullJsonThrowsMalformed()
    {
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("null"));
    }

    [Fact]
    public void NullErrorCodeFieldThrowsMalformed()
    {
        // Exercises the ProtocolErrorCodeConverter null-string guard
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("{\"type\":\"ERROR\",\"code\":null,\"message\":\"x\"}"));
    }

    private static void AssertRoundTrip(ControlMessage original)
    {
        string encoded = ControlMessageSerialization.Serialize(original);
        ControlMessage decoded = ControlMessageSerialization.Deserialize(encoded);
        Assert.Equal(original, decoded);
    }
}
