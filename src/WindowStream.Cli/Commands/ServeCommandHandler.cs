using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;

namespace WindowStream.Cli.Commands;

public sealed class ServeCommandHandler
{
    private readonly IWindowCaptureSource captureSource;
    private readonly ISessionHostLauncher hostLauncher;

    public ServeCommandHandler(IWindowCaptureSource captureSource, ISessionHostLauncher hostLauncher)
    {
        this.captureSource = captureSource;
        this.hostLauncher = hostLauncher;
    }

    public async Task<int> ExecuteAsync(ServeArguments arguments, CancellationToken cancellationToken)
    {
        WindowHandle? resolved = arguments.Handle;
        if (resolved is null && arguments.TitlePattern is not null)
        {
            var pattern = new Regex(arguments.TitlePattern, RegexOptions.CultureInvariant);
            var firstMatch = captureSource.ListWindows().FirstOrDefault(window => pattern.IsMatch(window.title));
            if (firstMatch is null)
            {
                return 2;
            }
            resolved = firstMatch.handle;
        }

        if (resolved is null)
        {
            return 2;
        }

        await hostLauncher.LaunchAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
