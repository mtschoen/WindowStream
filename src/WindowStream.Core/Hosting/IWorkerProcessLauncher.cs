using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

public interface IWorkerProcessLauncher
{
    Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken);
}
