using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Session.Testing;

public sealed class FakeSessionHostLauncherTests
{
    [Fact]
    public async Task Launch_Async_Records_Handle()
    {
        var launcher = new FakeSessionHostLauncher();
        var handle = new WindowHandle(42);

        await launcher.LaunchAsync(handle, CancellationToken.None);

        Assert.Equal(handle, launcher.LaunchedHandle);
    }
}
