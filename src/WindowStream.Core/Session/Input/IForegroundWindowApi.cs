namespace WindowStream.Core.Session.Input;

public interface IForegroundWindowApi
{
    long GetForegroundWindow();

    uint GetWindowThreadProcessId(long hwnd);

    bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

    bool SetForegroundWindow(long hwnd);

    uint CurrentThreadId();
}
