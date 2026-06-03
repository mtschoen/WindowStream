namespace WindowStream.Core.Hosting;

public interface IWorkerHandle : IAsyncDisposable
{
    Stream Pipe { get; }

    int ProcessId { get; }

    Task<int> WaitForExitAsync();

    void Kill();
}
