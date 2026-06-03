using System.Net;
using WindowStream.Core.Transport;

namespace WindowStream.Core.Session;

public interface IUdpVideoSender : IAsyncDisposable
{
    int LocalPort { get; }
    Task BindAsync(IPEndPoint localEndpoint, CancellationToken cancellationToken);
    Task SendPacketAsync(FragmentedPacket packet, IPEndPoint destination, CancellationToken cancellationToken);
}
