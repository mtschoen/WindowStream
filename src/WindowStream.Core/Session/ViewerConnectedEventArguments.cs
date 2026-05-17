using System;

namespace WindowStream.Core.Session;

public sealed class ViewerConnectedEventArguments : EventArgs
{
    public ViewerConnectedEventArguments(string endpoint)
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }
}
