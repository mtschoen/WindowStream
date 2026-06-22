namespace WindowStream.Core.Capture.Detection;

// Tuning for SourceFrameMonitor. Defaults derived from the WGC frame-delivery spike
// (spike_wgc-frame-delivery-map): idle first frame < 60ms; healthy ~50fps; throttle/minimize
// is a sharp cliff to 0, never a gradual sag - so a forgiving multiple-of-interval threshold
// cannot false-trigger on idle (which never climbs to a cadence).
public sealed record SourceFrameMonitorOptions(
    int StartupGraceMilliseconds,
    int MinimumFramesToEstablishCadence,
    int CliffMultiple,
    int StallFloorMilliseconds)
{
    public static SourceFrameMonitorOptions Default => new(
        StartupGraceMilliseconds: 2000,
        MinimumFramesToEstablishCadence: 8,
        CliffMultiple: 6,
        StallFloorMilliseconds: 100);
}

// Worker-side primary detector. Pure state machine: no I/O, injected clock. Caller invokes
// RecordFrame() per delivered frame and Evaluate() on a periodic tick, acting on the returned
// transition. Establishing -> Flowing -> Stalled -> Flowing.
public sealed class SourceFrameMonitor
{
    enum Phase { Idle, AwaitingFirstFrame, Establishing, Flowing, Stalled }

    readonly TimeProvider _timeProvider;
    readonly SourceFrameMonitorOptions _options;

    Phase _phase = Phase.Idle;
    long _startTimestamp;
    long _lastFrameTimestamp;
    int _frameCount;
    double _medianIntervalMilliseconds;
    readonly List<double> _intervalSamples = new();
    public SourceFrameMonitor(TimeProvider timeProvider, SourceFrameMonitorOptions options)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public StallCause LastStallCause { get; private set; }
    public long LastFrameAgeMilliseconds { get; private set; }

    public void Start()
    {
        _startTimestamp = NowMilliseconds();
        _phase = Phase.AwaitingFirstFrame;
    }

    public StallTransition RecordFrame()
    {
        var now = NowMilliseconds();
        var wasStalled = _phase == Phase.Stalled;

        if (_phase is Phase.AwaitingFirstFrame or Phase.Idle)
        {
            _phase = Phase.Establishing;
            _lastFrameTimestamp = now;
            _frameCount = 1;
            return StallTransition.None;
        }

        var interval = now - _lastFrameTimestamp;
        _lastFrameTimestamp = now;
        _frameCount++;
        RecordInterval(interval);

        if (_frameCount >= _options.MinimumFramesToEstablishCadence && _phase != Phase.Stalled)
        {
            _phase = Phase.Flowing;
        }

        if (wasStalled)
        {
            _phase = _frameCount >= _options.MinimumFramesToEstablishCadence ? Phase.Flowing : Phase.Establishing;
            return StallTransition.Resumed;
        }

        return StallTransition.None;
    }

    public StallTransition Evaluate()
    {
        var now = NowMilliseconds();

        // NeverStarted fires once: transitioning to Stalled exits AwaitingFirstFrame, so
        // the grace check runs only while still waiting for the first frame.
        if (_phase == Phase.AwaitingFirstFrame)
        {
            if (now - _startTimestamp >= _options.StartupGraceMilliseconds)
            {
                _phase = Phase.Stalled;
                LastStallCause = StallCause.NeverStarted;
                LastFrameAgeMilliseconds = now - _startTimestamp;
                return StallTransition.Stalled;
            }
            return StallTransition.None;
        }

        if (_phase == Phase.Flowing)
        {
            var threshold = Math.Max(
                _options.StallFloorMilliseconds,
                _options.CliffMultiple * _medianIntervalMilliseconds);
            var age = now - _lastFrameTimestamp;
            if (age >= threshold)
            {
                _phase = Phase.Stalled;
                LastStallCause = StallCause.SourceStalled;
                LastFrameAgeMilliseconds = age;
                return StallTransition.Stalled;
            }
        }

        return StallTransition.None;
    }

    void RecordInterval(double interval)
    {
        const int window = 32;
        _intervalSamples.Add(interval);
        if (_intervalSamples.Count > window)
        {
            _intervalSamples.RemoveAt(0);
        }
        var ordered = _intervalSamples.OrderBy(static value => value).ToArray();
        _medianIntervalMilliseconds = ordered[ordered.Length / 2];
    }

    long NowMilliseconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
}
