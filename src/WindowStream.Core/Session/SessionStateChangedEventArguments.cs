using System;

namespace WindowStream.Core.Session;

public sealed class SessionStateChangedEventArguments : EventArgs
{
    public SessionStateChangedEventArguments(SessionState fromState, SessionState toState)
    {
        FromState = fromState;
        ToState = toState;
    }

    public SessionState FromState { get; }
    public SessionState ToState { get; }
}
