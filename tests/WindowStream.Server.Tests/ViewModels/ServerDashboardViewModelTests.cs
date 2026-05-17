using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Session.Testing;
using WindowStream.Server.ViewModels;
using Xunit;

namespace WindowStream.Server.Tests.ViewModels;

public sealed class ServerDashboardViewModelTests
{
    [Fact]
    public void Initial_Server_Status_Is_Starting()
    {
        var viewModel = new ServerDashboardViewModel(new FakeSessionHostLauncher());

        Assert.Equal("Starting…", viewModel.ServerStatus);
    }

    [Fact]
    public async Task Start_Serving_Async_Transitions_Status_To_Stopped_On_Cancellation()
    {
        var viewModel = new ServerDashboardViewModel(new CancellingSessionHostLauncher());
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await viewModel.StartServingAsync(cancellationTokenSource.Token);

        Assert.Equal("Stopped", viewModel.ServerStatus);
    }

    [Fact]
    public async Task Start_Serving_Async_Reports_Error_On_Exception()
    {
        var launcher = new ThrowingSessionHostLauncher("kaboom");
        var viewModel = new ServerDashboardViewModel(launcher);

        await viewModel.StartServingAsync(CancellationToken.None);

        Assert.StartsWith("Error:", viewModel.ServerStatus);
    }

    [Fact]
    public void Report_Ports_Updates_Tcp_And_Udp()
    {
        var viewModel = new ServerDashboardViewModel(new FakeSessionHostLauncher());

        viewModel.ReportPorts(tcp: 9000, udp: 9001);

        Assert.Equal(9000, viewModel.TcpPort);
        Assert.Equal(9001, viewModel.UdpPort);
    }

    [Fact]
    public void Report_Connected_Viewer_Updates_Property()
    {
        var viewModel = new ServerDashboardViewModel(new FakeSessionHostLauncher());

        viewModel.ReportConnectedViewer("10.0.0.5:51001");

        Assert.Equal("10.0.0.5:51001", viewModel.ConnectedViewer);
    }

    [Fact]
    public void Report_Active_Streams_Updates_Count()
    {
        var viewModel = new ServerDashboardViewModel(new FakeSessionHostLauncher());

        viewModel.ReportActiveStreams(3);

        Assert.Equal(3, viewModel.ActiveStreamCount);
    }

    [Fact]
    public void Report_Available_Windows_Updates_Count()
    {
        var viewModel = new ServerDashboardViewModel(new FakeSessionHostLauncher());

        viewModel.ReportAvailableWindows(7);

        Assert.Equal(7, viewModel.AvailableWindowCount);
    }

    [Fact]
    public void Property_Changed_Fires_When_Status_Changes()
    {
        var viewModel = new ServerDashboardViewModel(new FakeSessionHostLauncher());
        string? lastChanged = null;
        viewModel.PropertyChanged += (_, eventArguments) => lastChanged = eventArguments.PropertyName;

        viewModel.ReportPorts(tcp: 1234, udp: 1235);

        Assert.Equal(nameof(ServerDashboardViewModel.UdpPort), lastChanged);
    }

    [Fact]
    public void Set_Field_Does_Not_Fire_When_Value_Unchanged()
    {
        var viewModel = new ServerDashboardViewModel(new FakeSessionHostLauncher());
        viewModel.ReportPorts(tcp: 9000, udp: 9001);
        int raised = 0;
        viewModel.PropertyChanged += (_, _) => raised++;

        viewModel.ReportPorts(tcp: 9000, udp: 9001);

        Assert.Equal(0, raised);
    }

    private sealed class CancellingSessionHostLauncher : WindowStream.Core.Session.ISessionHostLauncher
    {
        public Task LaunchAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : throw new System.InvalidOperationException("token must be cancelled"));
    }

    private sealed class ThrowingSessionHostLauncher : WindowStream.Core.Session.ISessionHostLauncher
    {
        private readonly string message;

        public ThrowingSessionHostLauncher(string message)
        {
            this.message = message;
        }

        public Task LaunchAsync(CancellationToken cancellationToken) =>
            throw new System.InvalidOperationException(message);
    }
}
