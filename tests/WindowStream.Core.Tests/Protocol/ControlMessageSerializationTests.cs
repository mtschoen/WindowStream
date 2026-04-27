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
    public void ServerHello_RoundTripsWithWindowsListAndUdpPort()
    {
        WindowDescriptor[] windows = new[]
        {
            new WindowDescriptor(1UL, 0x100, 99, "notepad", "Untitled - Notepad", 800, 600),
            new WindowDescriptor(2UL, 0x200, 100, "devenv", "WindowStream.sln", 1920, 1080)
        };
        ServerHelloMessage original = new ServerHelloMessage(ServerVersion: 2, UdpPort: 64000, Windows: windows);

        string serialized = ControlMessageSerialization.Serialize(original);
        ControlMessage deserialized = ControlMessageSerialization.Deserialize(serialized);

        ServerHelloMessage typed = Assert.IsType<ServerHelloMessage>(deserialized);
        Assert.Equal(2, typed.ServerVersion);
        Assert.Equal(64000, typed.UdpPort);
        Assert.Equal(2, typed.Windows.Length);
        Assert.Equal(1UL, typed.Windows[0].WindowId);
        Assert.Equal("Untitled - Notepad", typed.Windows[0].Title);
    }

    [Fact]
    public void StreamStarted_RoundTripsWithWindowId()
    {
        StreamStartedMessage original = new StreamStartedMessage(
            StreamId: 7,
            WindowId: 42UL,
            Codec: "h264",
            Width: 1920,
            Height: 1080,
            FramesPerSecond: 60);

        string serialized = ControlMessageSerialization.Serialize(original);
        StreamStartedMessage typed = Assert.IsType<StreamStartedMessage>(ControlMessageSerialization.Deserialize(serialized));

        Assert.Equal(7, typed.StreamId);
        Assert.Equal(42UL, typed.WindowId);
        Assert.Equal("h264", typed.Codec);
        Assert.Equal(1920, typed.Width);
        Assert.Equal(1080, typed.Height);
        Assert.Equal(60, typed.FramesPerSecond);
    }

    [Fact]
    public void StreamStopped_RoundTripsWithReason()
    {
        StreamStoppedMessage original = new StreamStoppedMessage(StreamId: 3, Reason: StreamStoppedReason.EncoderFailed);
        StreamStoppedMessage typed = Assert.IsType<StreamStoppedMessage>(
            ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
        Assert.Equal(3, typed.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, typed.Reason);
    }

    [Fact]
    public void ViewerReady_RoundTripsWithoutStreamId()
    {
        ViewerReadyMessage original = new ViewerReadyMessage(ViewerUdpPort: 12345);
        ViewerReadyMessage typed = Assert.IsType<ViewerReadyMessage>(
            ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
        Assert.Equal(12345, typed.ViewerUdpPort);
    }

    [Fact]
    public void KeyEvent_RoundTripsWithStreamId()
    {
        KeyEventMessage original = new KeyEventMessage(StreamId: 5, KeyCode: 0x41, IsUnicode: true, IsDown: true);
        KeyEventMessage typed = Assert.IsType<KeyEventMessage>(
            ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
        Assert.Equal(5, typed.StreamId);
        Assert.Equal(0x41, typed.KeyCode);
        Assert.True(typed.IsUnicode);
        Assert.True(typed.IsDown);
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
