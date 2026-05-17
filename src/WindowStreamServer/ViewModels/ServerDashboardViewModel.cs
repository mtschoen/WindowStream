using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using WindowStream.Core.Observability;
using WindowStream.Core.Session;
using WindowStream.Server.Observability;

namespace WindowStream.Server.ViewModels;

/// <summary>
/// Subscribes to the InAppDashboardSink and runs every event through the
/// ServerStateReducer to derive state-board bindings. Also maintains an
/// ObservableCollection of recent log entries for the event-log pane.
/// </summary>
public sealed class ServerDashboardViewModel : INotifyPropertyChanged
{
    private readonly ISessionHostLauncher hostLauncher;
    private readonly InAppDashboardSink sink;
    private readonly ServerStateReducer reducer = new();

    public ServerState State => reducer.State;
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
        this.hostLauncher = hostLauncher;
        this.sink = sink;
        foreach (LogEntry entry in sink.Snapshot()) AppendEntry(entry);
        sink.OnEvent += OnSinkEvent;
    }

    public async Task StartServingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await hostLauncher.LaunchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception exception)
        {
            // The diagnostics façade should already have logged details; this catch
            // exists so the page doesn't see an unhandled task exception.
            System.Diagnostics.Debug.WriteLine($"launcher faulted: {exception}");
        }
    }

    private void OnSinkEvent(LogEntry entry) =>
        MarshalEntryToMainThread(entry);

    // Separated so the MAUI-dispatcher call (which throws in headless test hosts)
    // can be excluded from coverage without losing coverage on the catch fallback path.
    [ExcludeFromCodeCoverage]
    private void MarshalEntryToMainThread(LogEntry entry)
    {
        // Sink fires from arbitrary threads; marshal to main for UI update.
        try
        {
            MainThread.BeginInvokeOnMainThread(() => AppendEntry(entry));
        }
        catch (Exception)
        {
            // No MAUI dispatcher available (e.g. headless unit-test host).
            // Fall back to a synchronous append so the entry is not lost.
            AppendEntry(entry);
        }
    }

    private void AppendEntry(LogEntry entry)
    {
        RecentEvents.Add(new LogEntryViewModel(entry));
        while (RecentEvents.Count > 200) RecentEvents.RemoveAt(0);
        RaiseAll();
    }

    public void ApplyEvent(PipelineEvent pipelineEvent)
    {
        reducer.Apply(pipelineEvent);
        MarshalRaiseAllToMainThread();
    }

    // Separated so the MAUI-dispatcher call (which throws in headless test hosts)
    // can be excluded from coverage without losing coverage of the catch fallback path.
    [ExcludeFromCodeCoverage]
    private void MarshalRaiseAllToMainThread()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(RaiseAll);
        }
        catch (Exception)
        {
            // No MAUI dispatcher available (e.g. headless unit-test host).
            // Fall back to a synchronous raise so callers see up-to-date
            // property-changed notifications immediately.
            RaiseAll();
        }
    }

    private void RaiseAll()
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
