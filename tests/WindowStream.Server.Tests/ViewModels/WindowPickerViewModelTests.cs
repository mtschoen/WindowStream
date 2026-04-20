using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using WindowStream.Core.Session.Testing;
using WindowStream.Server.ViewModels;
using Xunit;

namespace WindowStream.Server.Tests.ViewModels;

public sealed class WindowPickerViewModelTests
{
    [Fact]
    public void Refresh_Populates_Windows_From_Capture_Source()
    {
        var captureSource = new FakeWindowCaptureSource(new[]
        {
            new WindowInformation(new WindowHandle(1), "Alpha", "alpha", 1920, 1080),
            new WindowInformation(new WindowHandle(2), "Beta", "beta", 1280, 720),
        });
        var launcher = new FakeSessionHostLauncher();
        var viewModel = new WindowPickerViewModel(captureSource, launcher);

        viewModel.Refresh();

        Assert.Equal(2, viewModel.Windows.Count);
    }

    [Fact]
    public async Task Start_Stream_Invokes_Launcher_With_Selected_Window()
    {
        var information = new WindowInformation(new WindowHandle(99), "Rider", "rider", 1920, 1080);
        var captureSource = new FakeWindowCaptureSource(new[] { information });
        var launcher = new FakeSessionHostLauncher();
        var viewModel = new WindowPickerViewModel(captureSource, launcher);
        viewModel.Refresh();

        await viewModel.StartStreamAsync(information, CancellationToken.None);

        Assert.Equal(information.handle, launcher.LaunchedHandle);
    }

    [Fact]
    public void Refresh_Raises_Property_Changed_For_Windows()
    {
        var captureSource = new FakeWindowCaptureSource(System.Array.Empty<WindowInformation>());
        var launcher = new FakeSessionHostLauncher();
        var viewModel = new WindowPickerViewModel(captureSource, launcher);
        string? lastChanged = null;
        viewModel.PropertyChanged += (_, eventArguments) => lastChanged = eventArguments.PropertyName;

        viewModel.Refresh();

        Assert.Equal(nameof(WindowPickerViewModel.Windows), lastChanged);
    }
}
