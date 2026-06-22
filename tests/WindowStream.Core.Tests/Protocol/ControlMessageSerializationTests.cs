using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class ControlMessageSerializationTests
{
    [Fact]
    public void StreamStopped_NullReason_DeserializesAsMalformed()
    {
        // StreamStoppedReasonConverter throws JsonException on a null wire value,
        // which Deserialize wraps as MalformedMessageException.
        var payload = "{\"type\":\"STREAM_STOPPED\",\"streamId\":1,\"reason\":null}";
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize(payload));
    }

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
        var windows = new[]
        {
            new WindowDescriptor(1UL, 0x100, 99, "notepad", "Untitled - Notepad", 800, 600),
            new WindowDescriptor(2UL, 0x200, 100, "devenv", "WindowStream.sln", 1920, 1080)
        };
        var original = new ServerHelloMessage(ServerVersion: 2, UdpPort: 64000, Windows: windows);

        var serialized = ControlMessageSerialization.Serialize(original);
        var deserialized = ControlMessageSerialization.Deserialize(serialized);

        var typed = Assert.IsType<ServerHelloMessage>(deserialized);
        Assert.Equal(2, typed.ServerVersion);
        Assert.Equal(64000, typed.UdpPort);
        Assert.Equal(2, typed.Windows.Length);
        Assert.Equal(1UL, typed.Windows[0].WindowId);
        Assert.Equal("Untitled - Notepad", typed.Windows[0].Title);
    }

    [Fact]
    public void StreamStarted_RoundTripsWithWindowId()
    {
        var original = new StreamStartedMessage(
            StreamId: 7,
            WindowId: 42UL,
            Codec: "h264",
            Width: 1920,
            Height: 1080,
            FramesPerSecond: 60);

        var serialized = ControlMessageSerialization.Serialize(original);
        var typed = Assert.IsType<StreamStartedMessage>(ControlMessageSerialization.Deserialize(serialized));

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
        var original = new StreamStoppedMessage(StreamId: 3, Reason: StreamStoppedReason.EncoderFailed);
        var typed = Assert.IsType<StreamStoppedMessage>(
            ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
        Assert.Equal(3, typed.StreamId);
        Assert.Equal(StreamStoppedReason.EncoderFailed, typed.Reason);
    }

    [Fact]
    public void ViewerReady_RoundTripsWithoutStreamId()
    {
        var original = new ViewerReadyMessage(ViewerUdpPort: 12345);
        var typed = Assert.IsType<ViewerReadyMessage>(
            ControlMessageSerialization.Deserialize(ControlMessageSerialization.Serialize(original)));
        Assert.Equal(12345, typed.ViewerUdpPort);
    }

    [Fact]
    public void KeyEvent_RoundTripsWithStreamId()
    {
        var original = new KeyEventMessage(StreamId: 5, KeyCode: 0x41, IsUnicode: true, IsDown: true);
        var typed = Assert.IsType<KeyEventMessage>(
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
        var encoded = ControlMessageSerialization.Serialize(HeartbeatMessage.Instance);
        Assert.Equal("{\"type\":\"HEARTBEAT\"}", encoded);
    }

    [Fact]
    public void UnknownTypeThrowsMalformed()
    {
        var exception = Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize("{\"type\":\"WAT\"}"));
        Assert.Contains("WAT", exception.Message, StringComparison.Ordinal);
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

    [Fact]
    public void StreamStalled_round_trips_with_wire_cause()
    {
        var message = new StreamStalledMessage(7, StallCause.SourceStalled);
        var json = ControlMessageSerialization.Serialize(message);
        Assert.Contains("\"type\":\"STREAM_STALLED\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cause\":\"SOURCE_STALLED\"", json, StringComparison.Ordinal);
        var decoded = Assert.IsType<StreamStalledMessage>(ControlMessageSerialization.Deserialize(json));
        Assert.Equal(7, decoded.StreamId);
        Assert.Equal(StallCause.SourceStalled, decoded.Cause);
    }

    [Fact]
    public void StreamResumed_round_trips()
    {
        var message = new StreamResumedMessage(7);
        var json = ControlMessageSerialization.Serialize(message);
        Assert.Contains("\"type\":\"STREAM_RESUMED\"", json, StringComparison.Ordinal);
        var decoded = Assert.IsType<StreamResumedMessage>(ControlMessageSerialization.Deserialize(json));
        Assert.Equal(7, decoded.StreamId);
    }

    [Theory]
    [InlineData(StallCause.NeverStarted, "NEVER_STARTED")]
    [InlineData(StallCause.SourceStalled, "SOURCE_STALLED")]
    [InlineData(StallCause.WorkerSilent, "WORKER_SILENT")]
    public void StallCause_wire_names_round_trip(StallCause cause, string wire)
    {
        Assert.Equal(wire, StallCauseNames.ToWireName(cause));
        Assert.Equal(cause, StallCauseNames.Parse(wire));
    }

    [Fact]
    public void StallCauseNames_ToWireName_throws_for_out_of_range_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StallCauseNames.ToWireName((StallCause)999));
    }

    [Fact]
    public void StallCauseNames_Parse_throws_for_unknown_wire_name()
    {
        Assert.Throws<ArgumentException>(() => StallCauseNames.Parse("NOT_A_CAUSE"));
    }

    [Fact]
    public void StallCauseConverter_Read_throws_for_null_json_value()
    {
        // Exercises the null-string guard in StallCauseConverter.Read via the deserialize path.
        var payload = "{\"type\":\"STREAM_STALLED\",\"streamId\":1,\"cause\":null}";
        Assert.Throws<MalformedMessageException>(
            () => ControlMessageSerialization.Deserialize(payload));
    }

    static void AssertRoundTrip(ControlMessage original)
    {
        var encoded = ControlMessageSerialization.Serialize(original);
        var decoded = ControlMessageSerialization.Deserialize(encoded);
        Assert.Equal(original, decoded);
    }
}
