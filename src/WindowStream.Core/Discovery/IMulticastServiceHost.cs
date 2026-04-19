using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Discovery;

public interface IMulticastServiceHost : System.IAsyncDisposable
{
    Task StartAdvertisingAsync(
        string serviceInstance,
        string serviceType,
        int port,
        IReadOnlyList<string> textRecords,
        CancellationToken cancellationToken);

    Task StopAdvertisingAsync(CancellationToken cancellationToken);
}
