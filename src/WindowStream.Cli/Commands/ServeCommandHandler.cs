using WindowStream.Core.Session;

namespace WindowStream.Cli.Commands;

public sealed class ServeCommandHandler
{
    readonly ISessionHostLauncher _hostLauncher;

    public ServeCommandHandler(ISessionHostLauncher hostLauncher)
    {
        _hostLauncher = hostLauncher;
    }

    public async Task<int> ExecuteAsync(ServeArguments arguments, CancellationToken cancellationToken)
    {
        _ = arguments;
        await _hostLauncher.LaunchAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
