using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowStream.Server.ViewModels;

public sealed class SessionViewModel : INotifyPropertyChanged
{
    private SessionStatus status = SessionStatus.Idle;
    private double framesPerSecond;
    private int bitrateKilobitsPerSecond;
    private string? connectedViewerEndpoint;

    public SessionStatus Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public double FramesPerSecond
    {
        get => framesPerSecond;
        private set => SetField(ref framesPerSecond, value);
    }

    public int BitrateKilobitsPerSecond
    {
        get => bitrateKilobitsPerSecond;
        private set => SetField(ref bitrateKilobitsPerSecond, value);
    }

    public string? ConnectedViewerEndpoint
    {
        get => connectedViewerEndpoint;
        private set => SetField(ref connectedViewerEndpoint, value);
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

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
