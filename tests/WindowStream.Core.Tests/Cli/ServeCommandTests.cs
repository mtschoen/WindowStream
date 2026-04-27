using System.Threading;
using System.Threading.Tasks;
using WindowStream.Cli.Commands;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class ServeCommandTests
{
    [Fact]
    public async Task Serve_Invokes_Launcher_And_Returns_Zero()
    {
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(hostLauncher);

        var exitCode = await handler.ExecuteAsync(new ServeArguments(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(hostLauncher.Launched);
    }
}
