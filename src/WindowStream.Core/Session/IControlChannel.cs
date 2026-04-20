using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session;

public interface IControlChannel : IAsyncDisposable
{
    DateTimeOffset LastHeartbeatReceived { get; }

    /// <summary>
    /// IP address of the remote peer, when the underlying transport can supply one.
    /// Fake or in-memory channels return <c>null</c>.
    /// </summary>
    IPAddress? RemoteIpAddress => null;

    void NotifyHeartbeatReceived();
    Task SendAsync(ControlMessage message, CancellationToken cancellationToken);
    Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken);
}
