using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public class V2MessageRoundTripTests
{
    static T RoundTrip<T>(T message) where T : ControlMessage
    {
        var serialized = ControlMessageSerialization.Serialize(message);
        var deserialized = ControlMessageSerialization.Deserialize(serialized);
        return Assert.IsType<T>(deserialized);
    }

    [Fact]
    public void WindowAdded_RoundTrips()
    {
        var descriptor = new WindowDescriptor(1UL, 0x100, 99, "notepad", "test", 800, 600);
        var typed = RoundTrip(new WindowAddedMessage(descriptor));
        Assert.Equal(1UL, typed.Window.WindowId);
        Assert.Equal("test", typed.Window.Title);
    }

    [Fact]
    public void WindowRemoved_RoundTrips()
    {
        var typed = RoundTrip(new WindowRemovedMessage(WindowId: 7UL));
        Assert.Equal(7UL, typed.WindowId);
    }

    [Fact]
    public void WindowUpdated_RoundTrips()
    {
        var typed = RoundTrip(new WindowUpdatedMessage(
            WindowId: 7UL, Title: "new title", PhysicalWidth: 1280, PhysicalHeight: 720));
        Assert.Equal(7UL, typed.WindowId);
        Assert.Equal("new title", typed.Title);
        Assert.Equal(1280, typed.PhysicalWidth);
        Assert.Equal(720, typed.PhysicalHeight);
    }

    [Fact]
    public void WindowUpdated_OptionalFieldsAcceptNull()
    {
        var typed = RoundTrip(new WindowUpdatedMessage(
            WindowId: 7UL, Title: null, PhysicalWidth: null, PhysicalHeight: null));
        Assert.Null(typed.Title);
        Assert.Null(typed.PhysicalWidth);
        Assert.Null(typed.PhysicalHeight);
    }

    [Fact]
    public void WindowSnapshot_RoundTrips()
    {
        var windows = new[] { new WindowDescriptor(1UL, 0x100, 99, "n", "t", 800, 600) };
        var typed = RoundTrip(new WindowSnapshotMessage(windows));
        Assert.Single(typed.Windows);
    }

    [Fact]
    public void ListWindows_RoundTrips()
    {
        var typed = RoundTrip(new ListWindowsMessage());
        Assert.NotNull(typed);
    }

    [Fact]
    public void OpenStream_RoundTrips()
    {
        var typed = RoundTrip(new OpenStreamMessage(WindowId: 42UL));
        Assert.Equal(42UL, typed.WindowId);
    }

    [Fact]
    public void CloseStream_RoundTrips()
    {
        var typed = RoundTrip(new CloseStreamMessage(StreamId: 3));
        Assert.Equal(3, typed.StreamId);
    }

    [Fact]
    public void PauseStream_RoundTrips()
    {
        var typed = RoundTrip(new PauseStreamMessage(StreamId: 3));
        Assert.Equal(3, typed.StreamId);
    }

    [Fact]
    public void ResumeStream_RoundTrips()
    {
        var typed = RoundTrip(new ResumeStreamMessage(StreamId: 3));
        Assert.Equal(3, typed.StreamId);
    }

    [Fact]
    public void FocusWindow_RoundTrips()
    {
        var typed = RoundTrip(new FocusWindowMessage(StreamId: 3));
        Assert.Equal(3, typed.StreamId);
    }
}
