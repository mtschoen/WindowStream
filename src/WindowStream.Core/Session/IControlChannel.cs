using System;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Session;

public interface IControlChannel : IAsyncDisposable
{
    DateTimeOffset LastHeartbeatReceived { get; }
    void NotifyHeartbeatReceived();
    Task SendAsync(ControlMessage message, CancellationToken cancellationToken);
    Task<ControlMessage> ReceiveAsync(CancellationToken cancellationToken);
}
