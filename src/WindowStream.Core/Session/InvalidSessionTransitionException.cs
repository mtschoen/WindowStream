using System;

namespace WindowStream.Core.Session;

public sealed class InvalidSessionTransitionException : InvalidOperationException
{
    public InvalidSessionTransitionException(SessionState fromState, SessionState toState)
        : base($"invalid session transition: {fromState} -> {toState}")
    {
        FromState = fromState;
        ToState = toState;
    }

    public SessionState FromState { get; }
    public SessionState ToState { get; }
}
