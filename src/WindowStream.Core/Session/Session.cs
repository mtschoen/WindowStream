namespace WindowStream.Core.Session;

public sealed class Session
{
    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    public event EventHandler<SessionStateChangedEventArguments>? StateChanged;

    public void BeginCapturing()
    {
        TransitionTo(SessionState.Capturing, allowedFromStates: new[] { SessionState.Idle });
    }

    public void BeginStreaming()
    {
        TransitionTo(SessionState.Streaming, allowedFromStates: new[] { SessionState.Capturing });
    }

    public void ViewerDisconnected()
    {
        TransitionTo(SessionState.Capturing, allowedFromStates: new[] { SessionState.Streaming });
    }

    public void Stop()
    {
        TransitionTo(SessionState.Stopped, allowedFromStates: new[] { SessionState.Capturing, SessionState.Streaming });
    }

    void TransitionTo(SessionState toState, SessionState[] allowedFromStates)
    {
        var allowed = false;
        for (var index = 0; index < allowedFromStates.Length; index++)
        {
            if (allowedFromStates[index] == CurrentState)
            {
                allowed = true;
                break;
            }
        }
        if (!allowed)
        {
            throw new InvalidSessionTransitionException(CurrentState, toState);
        }
        var fromState = CurrentState;
        CurrentState = toState;
        StateChanged?.Invoke(this, new SessionStateChangedEventArguments(fromState, toState));
    }
}
