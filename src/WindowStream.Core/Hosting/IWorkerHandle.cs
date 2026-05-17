using System;
using System.IO;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public interface IWorkerHandle : IAsyncDisposable
{
    Stream Pipe { get; }

    int ProcessId { get; }

    Task<int> WaitForExitAsync();

    void Kill();
}
