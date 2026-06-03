using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowStream.Server.ViewModels;

public sealed partial class SessionViewModel : INotifyPropertyChanged
{
    SessionStatus _status = SessionStatus.Starting;
    double _framesPerSecond;
    int _bitrateKilobitsPerSecond;
    string? _connectedViewerEndpoint;

    public SessionStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public double FramesPerSecond
    {
        get => _framesPerSecond;
        private set => SetField(ref _framesPerSecond, value);
    }

    public int BitrateKilobitsPerSecond
    {
        get => _bitrateKilobitsPerSecond;
        private set => SetField(ref _bitrateKilobitsPerSecond, value);
    }

    public string? ConnectedViewerEndpoint
    {
        get => _connectedViewerEndpoint;
        private set => SetField(ref _connectedViewerEndpoint, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ReportStatus(SessionStatus newStatus)
    {
        Status = newStatus;
    }

    public void ReportMetrics(SessionMetrics metrics)
    {
        FramesPerSecond = metrics.FramesPerSecond;
        BitrateKilobitsPerSecond = metrics.BitrateKilobitsPerSecond;
        ConnectedViewerEndpoint = metrics.ConnectedViewerEndpoint;
    }

    void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
