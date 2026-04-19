namespace WindowStream.Core.Session;

public interface ISessionObserver
{
    void OnStateChanged(SessionState fromState, SessionState toState);
}
