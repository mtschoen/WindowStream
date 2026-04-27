using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Session;

namespace WindowStream.Cli.Commands;

public sealed class ServeCommandHandler
{
    private readonly ISessionHostLauncher hostLauncher;

    public ServeCommandHandler(ISessionHostLauncher hostLauncher)
    {
        this.hostLauncher = hostLauncher;
    }

    public async Task<int> ExecuteAsync(ServeArguments arguments, CancellationToken cancellationToken)
    {
        _ = arguments;
        await hostLauncher.LaunchAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
