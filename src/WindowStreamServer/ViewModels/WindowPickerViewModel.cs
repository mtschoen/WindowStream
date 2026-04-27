using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;

namespace WindowStream.Server.ViewModels;

/// <summary>
/// v1-era picker shell. With v2 the viewer (not the server-side GUI) selects
/// the window remotely via OPEN_STREAM, so <see cref="StartStreamAsync"/>
/// just kicks off the parameterless coordinator. The selected
/// <see cref="WindowInformation"/> is retained for display only.
/// </summary>
public sealed class WindowPickerViewModel : INotifyPropertyChanged
{
    private readonly IWindowCaptureSource captureSource;
    private readonly ISessionHostLauncher hostLauncher;

    public ObservableCollection<WindowInformation> Windows { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public WindowPickerViewModel(IWindowCaptureSource captureSource, ISessionHostLauncher hostLauncher)
    {
        this.captureSource = captureSource;
        this.hostLauncher = hostLauncher;
    }

    public void Refresh()
    {
        Windows.Clear();
        foreach (WindowInformation window in captureSource.ListWindows())
        {
            Windows.Add(window);
        }

        OnPropertyChanged(nameof(Windows));
    }

    public Task StartStreamAsync(WindowInformation window, CancellationToken cancellationToken)
    {
        _ = window;
        return hostLauncher.LaunchAsync(cancellationToken);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
