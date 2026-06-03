namespace WindowStream.Core.Hosting;

public interface IWorkerProcessLauncher
{
    Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken);
}
