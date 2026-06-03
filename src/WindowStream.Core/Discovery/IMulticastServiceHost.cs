namespace WindowStream.Core.Discovery;

public interface IMulticastServiceHost : IAsyncDisposable
{
    Task StartAdvertisingAsync(
        string serviceInstance,
        string serviceType,
        int port,
        IReadOnlyList<string> textRecords,
        CancellationToken cancellationToken);

    Task StopAdvertisingAsync(CancellationToken cancellationToken);
}
