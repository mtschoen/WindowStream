using System.Collections.Generic;
using WindowStream.Core.Session;
using CoreSession = WindowStream.Core.Session.Session;
using Xunit;

namespace WindowStream.Core.Tests.Session;

public sealed class SessionTests
{
    [Fact]
    public void NewSessionStartsIdle()
    {
        CoreSession session = new();
        Assert.Equal(SessionState.Idle, session.CurrentState);
    }

    [Fact]
    public void HappyPathTransitionsIdleToCapturingToStreamingToStopped()
    {
        CoreSession session = new();
        session.BeginCapturing();
        Assert.Equal(SessionState.Capturing, session.CurrentState);
        session.BeginStreaming();
        Assert.Equal(SessionState.Streaming, session.CurrentState);
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.CurrentState);
    }

    [Fact]
    public void TransitionsRaiseStateChangedEventWithOldAndNew()
    {
        CoreSession session = new();
        List<SessionStateChangedEventArguments> transitions = new();
        session.StateChanged += (_, eventArguments) => transitions.Add(eventArguments);
        session.BeginCapturing();
        session.BeginStreaming();
        session.Stop();
        Assert.Equal(3, transitions.Count);
        Assert.Equal(SessionState.Idle, transitions[0].FromState);
        Assert.Equal(SessionState.Capturing, transitions[0].ToState);
        Assert.Equal(SessionState.Capturing, transitions[1].FromState);
        Assert.Equal(SessionState.Streaming, transitions[1].ToState);
        Assert.Equal(SessionState.Streaming, transitions[2].FromState);
        Assert.Equal(SessionState.Stopped, transitions[2].ToState);
    }

    [Fact]
    public void ViewerDisconnectedFromStreamingReturnsToCapturing()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        session.ViewerDisconnected();
        Assert.Equal(SessionState.Capturing, session.CurrentState);
    }

    [Fact]
    public void ViewerReconnectedFromCapturingReturnsToStreaming()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        session.ViewerDisconnected();
        session.BeginStreaming();  // reconnect path is the same transition as initial viewer connect
        Assert.Equal(SessionState.Streaming, session.CurrentState);
    }

    [Fact]
    public void BeginCapturingFromCapturingThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        InvalidSessionTransitionException exception = Assert.Throws<InvalidSessionTransitionException>(
            () => session.BeginCapturing());
        Assert.Equal(SessionState.Capturing, exception.FromState);
        Assert.Equal(SessionState.Capturing, exception.ToState);
    }

    [Fact]
    public void BeginCapturingFromStreamingThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginCapturing());
    }

    [Fact]
    public void BeginCapturingFromStoppedThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginCapturing());
    }

    [Fact]
    public void BeginStreamingFromIdleThrows()
    {
        CoreSession session = new();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginStreaming());
    }

    [Fact]
    public void BeginStreamingFromStreamingThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginStreaming());
    }

    [Fact]
    public void BeginStreamingFromStoppedThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.BeginStreaming());
    }

    [Fact]
    public void ViewerDisconnectedFromIdleThrows()
    {
        CoreSession session = new();
        Assert.Throws<InvalidSessionTransitionException>(() => session.ViewerDisconnected());
    }

    [Fact]
    public void ViewerDisconnectedFromCapturingThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        Assert.Throws<InvalidSessionTransitionException>(() => session.ViewerDisconnected());
    }

    [Fact]
    public void ViewerDisconnectedFromStoppedThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.ViewerDisconnected());
    }

    [Fact]
    public void StopFromIdleThrows()
    {
        CoreSession session = new();
        Assert.Throws<InvalidSessionTransitionException>(() => session.Stop());
    }

    [Fact]
    public void StopFromCapturingSucceeds()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.CurrentState);
    }

    [Fact]
    public void StopFromStreamingSucceeds()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.BeginStreaming();
        session.Stop();
        Assert.Equal(SessionState.Stopped, session.CurrentState);
    }

    [Fact]
    public void StopFromStoppedThrows()
    {
        CoreSession session = new();
        session.BeginCapturing();
        session.Stop();
        Assert.Throws<InvalidSessionTransitionException>(() => session.Stop());
    }

    [Fact]
    public void EventArgumentsRecordValuesMatchProperties()
    {
        SessionStateChangedEventArguments arguments = new(SessionState.Idle, SessionState.Capturing);
        Assert.Equal(SessionState.Idle, arguments.FromState);
        Assert.Equal(SessionState.Capturing, arguments.ToState);
    }
}
