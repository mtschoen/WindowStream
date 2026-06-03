using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using WindowStream.Core.Observability;
using WindowStream.Core.Session;
using WindowStream.Server.Observability;

namespace WindowStream.Server.ViewModels;

/// <summary>
/// Subscribes to the InAppDashboardSink and runs every event through the
/// ServerStateReducer to derive state-board bindings. Also maintains an
/// ObservableCollection of recent log entries for the event-log pane.
/// </summary>
public sealed partial class ServerDashboardViewModel : INotifyPropertyChanged
{
    readonly ISessionHostLauncher _hostLauncher;
    // Retained to anchor the sink for the OnEvent subscription's lifetime.
    // ReSharper disable once NotAccessedField.Local
    readonly InAppDashboardSink _sink;
    readonly ServerStateReducer _reducer = new();

    public ServerState State => _reducer.State;
    public ObservableCollection<LogEntryViewModel> RecentEvents { get; } = new();

    public string ServerStatus => State.Listening == StageStatus.Ok ? "Serving" : "Starting…";
    public int TcpPort => State.TcpPort ?? 0;
    public int UdpPort => State.UdpPort ?? 0;
    public string? ConnectedViewer => State.ViewerEndpoint;
    public int ActiveStreamCount => State.Streams.Count;
    public int AvailableWindowCount => State.WindowCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerDashboardViewModel(ISessionHostLauncher hostLauncher, InAppDashboardSink sink)
    {
        _hostLauncher = hostLauncher;
        _sink = sink;
        foreach (var entry in sink.Snapshot()) AppendEntry(entry);
        sink.OnEvent += OnSinkEvent;
    }

    public async Task StartServingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _hostLauncher.LaunchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
#pragma warning disable CA1031 // MAUI UI boundary: prevents unhandled task exception on the page; launcher details already logged by diagnostics façade
        catch (Exception exception)
        {
            // The diagnostics façade should already have logged details; this catch
            // exists so the page doesn't see an unhandled task exception.
            Debug.WriteLine($"launcher faulted: {exception}");
        }
#pragma warning restore CA1031
    }

    void OnSinkEvent(LogEntry entry) =>
        MarshalEntryToMainThread(entry);

    // The MAUI BeginInvokeOnMainThread call inside the try is uncoverable in headless
    // xUnit: WinRT's DispatcherQueue factory throws COMException ("ClassFactory cannot
    // supply requested class") before the dispatcher can run. The synchronous catch
    // fallback IS exercised by On_Sink_Event_Appends_Entry_Synchronously_In_Headless_Host.
    // We attribute the whole method so the dispatcher call site doesn't keep the
    // line-coverage gate red.
    [ExcludeFromCodeCoverage]
    void MarshalEntryToMainThread(LogEntry entry)
    {
        // Sink fires from arbitrary threads; marshal to main for UI update.
        try
        {
            MainThread.BeginInvokeOnMainThread(() => AppendEntry(entry));
        }
        catch (COMException)
        {
            // No MAUI dispatcher available (e.g. headless unit-test host —
            // WinRT factory throws). Fall back to a synchronous append so the
            // entry is not lost.
            AppendEntry(entry);
        }
    }

    void AppendEntry(LogEntry entry)
    {
        RecentEvents.Add(new LogEntryViewModel(entry));
        while (RecentEvents.Count > 200) RecentEvents.RemoveAt(0);
        RaiseAll();
    }

    public void ApplyEvent(PipelineEvent pipelineEvent)
    {
        _reducer.Apply(pipelineEvent);
        MarshalRaiseAllToMainThread();
    }

    // The MAUI BeginInvokeOnMainThread call inside the try is uncoverable in headless
    // xUnit: WinRT's DispatcherQueue factory throws COMException ("ClassFactory cannot
    // supply requested class") before the dispatcher can run. The synchronous catch
    // fallback IS exercised by Apply_Event_Fires_Property_Changed_Synchronously_In_Headless_Host.
    // We attribute the whole method so the dispatcher call site doesn't keep the
    // line-coverage gate red.
    [ExcludeFromCodeCoverage]
    void MarshalRaiseAllToMainThread()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(RaiseAll);
        }
        catch (COMException)
        {
            // No MAUI dispatcher available (e.g. headless unit-test host —
            // WinRT factory throws). Fall back to a synchronous raise so callers
            // see up-to-date property-changed notifications immediately.
            RaiseAll();
        }
    }

    void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TcpPort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UdpPort)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectedViewer)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveStreamCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableWindowCount)));
    }
}
