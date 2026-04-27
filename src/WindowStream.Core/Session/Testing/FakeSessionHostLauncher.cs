using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Session.Testing;

public sealed class FakeSessionHostLauncher : ISessionHostLauncher
{
    public bool Launched { get; private set; }

    public Task LaunchAsync(CancellationToken cancellationToken)
    {
        Launched = true;
        return Task.CompletedTask;
    }
}
