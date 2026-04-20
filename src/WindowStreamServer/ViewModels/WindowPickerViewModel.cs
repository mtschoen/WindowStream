using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Session;

namespace WindowStream.Server.ViewModels;

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
        return hostLauncher.LaunchAsync(window.handle, cancellationToken);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
