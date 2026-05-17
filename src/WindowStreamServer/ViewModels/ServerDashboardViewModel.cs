using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Session;

namespace WindowStream.Server.ViewModels;

/// <summary>
/// View model for the server status dashboard. Tracks the coordinator lifecycle,
/// connected viewer, and active stream count. Unlike the v1 picker, no user
/// interaction is required — the coordinator starts automatically.
/// </summary>
public sealed class ServerDashboardViewModel : INotifyPropertyChanged
{
    private readonly ISessionHostLauncher hostLauncher;

    private string serverStatus = "Starting…";
    private int tcpPort;
    private int udpPort;
    private string? connectedViewer;
    private int activeStreamCount;
    private int availableWindowCount;

    public string ServerStatus
    {
        get => serverStatus;
        private set => SetField(ref serverStatus, value);
    }

    public int TcpPort
    {
        get => tcpPort;
        private set => SetField(ref tcpPort, value);
    }

    public int UdpPort
    {
        get => udpPort;
        private set => SetField(ref udpPort, value);
    }

    public string? ConnectedViewer
    {
        get => connectedViewer;
        private set => SetField(ref connectedViewer, value);
    }

    public int ActiveStreamCount
    {
        get => activeStreamCount;
        private set => SetField(ref activeStreamCount, value);
    }

    public int AvailableWindowCount
    {
        get => availableWindowCount;
        private set => SetField(ref availableWindowCount, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerDashboardViewModel(ISessionHostLauncher hostLauncher)
    {
        this.hostLauncher = hostLauncher;
    }

    /// <summary>
    /// Launches the coordinator and updates status. Intended to be called once
    /// from the page lifecycle (OnAppearing). Runs until cancellation.
    /// </summary>
    public async Task StartServingAsync(CancellationToken cancellationToken)
    {
        ServerStatus = "Serving";
        try
        {
            await hostLauncher.LaunchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
        {
            ServerStatus = "Stopped";
        }
        catch (System.Exception exception)
        {
            ServerStatus = $"Error: {exception.Message}";
        }
    }

    public void ReportPorts(int tcp, int udp)
    {
        TcpPort = tcp;
        UdpPort = udp;
    }

    public void ReportConnectedViewer(string? endpoint)
    {
        ConnectedViewer = endpoint;
    }

    public void ReportActiveStreams(int count)
    {
        ActiveStreamCount = count;
    }

    public void ReportAvailableWindows(int count)
    {
        AvailableWindowCount = count;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
