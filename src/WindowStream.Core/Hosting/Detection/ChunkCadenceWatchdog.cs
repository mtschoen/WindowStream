using WindowStream.Core.Capture.Detection;

namespace WindowStream.Core.Hosting.Detection;

// Tuning for the coordinator-side safety-net watchdog. SilentFloor is deliberately longer
// than the worker SourceFrameMonitor thresholds so the worker always self-reports first; the
// watchdog only fires when the worker itself is wedged and cannot report.
public sealed record ChunkCadenceWatchdogOptions(
    int StartupGraceMilliseconds,
    int SilentFloorMilliseconds)
{
    public static ChunkCadenceWatchdogOptions Default => new(
        StartupGraceMilliseconds: 2000,
        SilentFloorMilliseconds: 3000);
}

// Coordinator-side per-stream detector. Pure state machine. Only knows "chunks stopped"; its
// job is the failure mode the worker cannot report (worker deadlocked / crashed mid-frame).
public sealed class ChunkCadenceWatchdog
{
    readonly TimeProvider _timeProvider;
    readonly ChunkCadenceWatchdogOptions _options;

    bool _sawChunk;
    bool _stalled;
    bool _suppressed;
    long _lastChunkTimestamp;

    public ChunkCadenceWatchdog(TimeProvider timeProvider, ChunkCadenceWatchdogOptions options)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public StallCause LastStallCause { get; private set; }

    public void RecordChunk()
    {
        _sawChunk = true;
        _lastChunkTimestamp = NowMilliseconds();
    }

    public void SetWorkerReportedStalled(bool stalled) => _suppressed = stalled;

    public StallTransition Evaluate()
    {
        if (!_sawChunk || _suppressed)
        {
            return StallTransition.None;
        }

        var age = NowMilliseconds() - _lastChunkTimestamp;

        if (_stalled)
        {
            if (age < _options.SilentFloorMilliseconds)
            {
                _stalled = false;
                return StallTransition.Resumed;
            }
            return StallTransition.None;
        }

        if (age >= _options.SilentFloorMilliseconds)
        {
            _stalled = true;
            LastStallCause = StallCause.WorkerSilent;
            return StallTransition.Stalled;
        }

        return StallTransition.None;
    }

    long NowMilliseconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
}
