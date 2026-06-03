namespace WindowStream.Core.Session.Input;

public sealed class FocusRelay
{
    readonly IForegroundWindowApi _api;

    public FocusRelay(IForegroundWindowApi api)
    {
        _api = api;
    }

    public bool BringToForeground(long hwnd)
    {
        var currentForeground = _api.GetForegroundWindow();
        if (currentForeground == hwnd)
        {
            return true;
        }

        var currentThread = _api.GetWindowThreadProcessId(currentForeground);
        var myThread = _api.CurrentThreadId();
        _api.AttachThreadInput(myThread, currentThread, true);
        try
        {
            return _api.SetForegroundWindow(hwnd);
        }
        finally
        {
            _api.AttachThreadInput(myThread, currentThread, false);
        }
    }
}
