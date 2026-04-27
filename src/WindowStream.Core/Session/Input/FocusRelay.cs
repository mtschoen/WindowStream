namespace WindowStream.Core.Session.Input;

public sealed class FocusRelay
{
    private readonly IForegroundWindowApi api;

    public FocusRelay(IForegroundWindowApi api)
    {
        this.api = api;
    }

    public bool BringToForeground(long hwnd)
    {
        long currentForeground = api.GetForegroundWindow();
        if (currentForeground == hwnd)
        {
            return true;
        }

        uint currentThread = api.GetWindowThreadProcessId(currentForeground);
        uint myThread = api.CurrentThreadId();
        api.AttachThreadInput(myThread, currentThread, true);
        try
        {
            return api.SetForegroundWindow(hwnd);
        }
        finally
        {
            api.AttachThreadInput(myThread, currentThread, false);
        }
    }
}
