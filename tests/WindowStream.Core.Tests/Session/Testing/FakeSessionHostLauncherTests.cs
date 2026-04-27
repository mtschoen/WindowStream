using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session.Testing;

public sealed class FakeSessionHostLauncherTests
{
    [Fact]
    public async Task Launch_Async_Records_Invocation()
    {
        var launcher = new FakeSessionHostLauncher();

        await launcher.LaunchAsync(CancellationToken.None);

        Assert.True(launcher.Launched);
    }
}
