using System;

namespace WindowStream.Core.Session;

public sealed class ViewerDisconnectedEventArguments : EventArgs
{
    public ViewerDisconnectedEventArguments(string endpoint, string reason = "disconnected")
    {
        Endpoint = endpoint;
        Reason = reason;
    }

    public string Endpoint { get; }

    public string Reason { get; }
}
