using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Core.Session.Testing;

public sealed class FakeSessionHostLauncher : ISessionHostLauncher
{
    public WindowHandle? LaunchedHandle { get; private set; }

    public Task LaunchAsync(WindowHandle handle, CancellationToken cancellationToken)
    {
        LaunchedHandle = handle;
        return Task.CompletedTask;
    }
}
