using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Session;

/// <summary>
/// Launch-and-serve contract used by the CLI / picker GUI to start the
/// production server process. v2 launchers are parameterless — the viewer
/// (not the server-side caller) selects the window via the OPEN_STREAM
/// control message after connecting.
/// </summary>
public interface ISessionHostLauncher
{
    Task LaunchAsync(CancellationToken cancellationToken);
}
