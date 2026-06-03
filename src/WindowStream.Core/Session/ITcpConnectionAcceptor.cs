namespace WindowStream.Core.Session;

public interface ITcpConnectionAcceptor : IAsyncDisposable
{
    int LocalPort { get; }
    void StartListening(int port);
    Task<IControlChannel> AcceptAsync(CancellationToken cancellationToken);
}
