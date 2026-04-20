using System.ComponentModel;
using WindowStream.Server.ViewModels;
using Xunit;

namespace WindowStream.Server.Tests.ViewModels;

public sealed class SessionViewModelTests
{
    [Fact]
    public void Initial_State_Is_Idle()
    {
        var viewModel = new SessionViewModel();

        Assert.Equal(SessionStatus.Idle, viewModel.Status);
    }

    [Fact]
    public void Observed_Metrics_Update_Property_Changed()
    {
        var viewModel = new SessionViewModel();
        string? lastChanged = null;
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, eventArguments) => lastChanged = eventArguments.PropertyName;

        viewModel.ReportMetrics(new SessionMetrics(
            FramesPerSecond: 59.9,
            BitrateKilobitsPerSecond: 6500,
            ConnectedViewerEndpoint: "192.168.1.44:51001"));

        Assert.Equal(59.9, viewModel.FramesPerSecond);
        Assert.Equal(6500, viewModel.BitrateKilobitsPerSecond);
        Assert.Equal("192.168.1.44:51001", viewModel.ConnectedViewerEndpoint);
        Assert.Equal(nameof(SessionViewModel.ConnectedViewerEndpoint), lastChanged);
    }

    [Fact]
    public void Stop_Transitions_To_Idle()
    {
        var viewModel = new SessionViewModel();
        viewModel.ReportStatus(SessionStatus.Streaming);
        viewModel.ReportStatus(SessionStatus.Idle);

        Assert.Equal(SessionStatus.Idle, viewModel.Status);
    }

    [Fact]
    public void Report_Status_Raises_Property_Changed()
    {
        var viewModel = new SessionViewModel();
        string? lastChanged = null;
        viewModel.PropertyChanged += (_, eventArguments) => lastChanged = eventArguments.PropertyName;

        viewModel.ReportStatus(SessionStatus.Streaming);

        Assert.Equal(nameof(SessionViewModel.Status), lastChanged);
    }

    [Fact]
    public void Set_Field_Does_Not_Raise_Property_Changed_When_Value_Unchanged()
    {
        var viewModel = new SessionViewModel();
        int raised = 0;
        viewModel.PropertyChanged += (_, _) => raised++;

        viewModel.ReportStatus(SessionStatus.Idle);

        Assert.Equal(0, raised);
    }
}
