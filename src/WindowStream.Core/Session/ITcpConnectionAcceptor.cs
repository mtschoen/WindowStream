using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Session;

public interface ITcpConnectionAcceptor : System.IAsyncDisposable
{
    int LocalPort { get; }
    void StartListening(int port);
    Task<IControlChannel> AcceptAsync(CancellationToken cancellationToken);
}
