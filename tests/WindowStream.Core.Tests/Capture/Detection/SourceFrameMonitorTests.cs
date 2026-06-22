using Microsoft.Extensions.Time.Testing;
using WindowStream.Core.Capture.Detection;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Detection;

public sealed class SourceFrameMonitorTests
{
    static SourceFrameMonitor Build(FakeTimeProvider time) =>
        new(time, SourceFrameMonitorOptions.Default);

    [Fact]
    public void NeverStarted_when_no_frame_within_startup_grace()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();

        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Equal(StallTransition.None, monitor.Evaluate());

        time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
        Assert.Equal(StallCause.NeverStarted, monitor.LastStallCause);
    }

    [Fact]
    public void NeverStarted_only_emits_once()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        time.Advance(TimeSpan.FromMilliseconds(2500));
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(StallTransition.None, monitor.Evaluate());
    }

    [Fact]
    public void First_frame_clears_startup_grace()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(StallTransition.None, monitor.RecordFrame());
        time.Advance(TimeSpan.FromMilliseconds(5000));
        // One frame then quiet (idle window) never establishes cadence -> never stalls.
        Assert.Equal(StallTransition.None, monitor.Evaluate());
    }

    [Fact]
    public void Idle_window_never_false_triggers_stall()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        // Two frames, far apart - never reaches MinimumFramesToEstablishCadence.
        monitor.RecordFrame();
        time.Advance(TimeSpan.FromMilliseconds(3000));
        monitor.RecordFrame();
        time.Advance(TimeSpan.FromMilliseconds(60000));
        Assert.Equal(StallTransition.None, monitor.Evaluate());
    }

    [Fact]
    public void Cadence_cliff_after_established_rate_emits_stall()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        // Establish ~50fps (20ms interval) over >=8 frames.
        for (var frame = 0; frame < 10; frame++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            Assert.Equal(StallTransition.None, monitor.RecordFrame());
        }
        // Gap of 6 * 20ms = 120ms is the threshold; cross it.
        time.Advance(TimeSpan.FromMilliseconds(121));
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
        Assert.Equal(StallCause.SourceStalled, monitor.LastStallCause);
        Assert.True(monitor.LastFrameAgeMilliseconds >= 121);
    }

    [Fact]
    public void Stall_floor_prevents_false_trigger_at_low_fps()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        // Establish ~5fps (200ms interval). 6 * 200 = 1200ms > 1000ms floor, so threshold = 1200.
        for (var frame = 0; frame < 10; frame++)
        {
            time.Advance(TimeSpan.FromMilliseconds(200));
            monitor.RecordFrame();
        }
        time.Advance(TimeSpan.FromMilliseconds(1100));
        Assert.Equal(StallTransition.None, monitor.Evaluate());
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
    }

    [Fact]
    public void Frame_after_stall_emits_resume()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        for (var frame = 0; frame < 10; frame++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            monitor.RecordFrame();
        }
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
        time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(StallTransition.Resumed, monitor.RecordFrame());
        // Back to flowing: a fresh cliff stalls again.
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
    }

    [Fact]
    public void Evaluate_while_flowing_returns_none()
    {
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        for (var frame = 0; frame < 10; frame++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            monitor.RecordFrame();
        }
        time.Advance(TimeSpan.FromMilliseconds(40));
        Assert.Equal(StallTransition.None, monitor.Evaluate());
    }

    [Fact]
    public void Constructor_rejects_null_time_provider()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SourceFrameMonitor(null!, SourceFrameMonitorOptions.Default));
    }

    [Fact]
    public void Constructor_rejects_null_options()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SourceFrameMonitor(new FakeTimeProvider(), null!));
    }

    [Fact]
    public void Resume_before_cadence_established_stays_in_establishing()
    {
        // Stall before MinimumFramesToEstablishCadence is reached, then recover with
        // fewer than MinimumFramesToEstablishCadence total frames - resume should not
        // transition to Flowing (stays Establishing, so cadence cliff cannot fire).
        var time = new FakeTimeProvider();
        var options = new SourceFrameMonitorOptions(
            StartupGraceMilliseconds: 500,
            MinimumFramesToEstablishCadence: 8,
            CliffMultiple: 6,
            StallFloorMilliseconds: 100);
        var monitor = new SourceFrameMonitor(time, options);
        monitor.Start();
        time.Advance(TimeSpan.FromMilliseconds(600));
        // NeverStarted fires.
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
        // One frame arrives - frameCount=1, still below minimum; returns Resumed but stays Establishing.
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(StallTransition.Resumed, monitor.RecordFrame());
        // Long silence: no cadence was established so no cliff fires.
        time.Advance(TimeSpan.FromMilliseconds(60000));
        Assert.Equal(StallTransition.None, monitor.Evaluate());
    }

    [Fact]
    public void Rolling_window_evicts_oldest_sample_beyond_32_frames()
    {
        // Drive 40 frames through at 20ms to exercise the >32 sample eviction path.
        var time = new FakeTimeProvider();
        var monitor = Build(time);
        monitor.Start();
        for (var frame = 0; frame < 40; frame++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            monitor.RecordFrame();
        }
        // Cadence is fully established and window eviction ran; a cliff gap still stalls.
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(StallTransition.Stalled, monitor.Evaluate());
        Assert.Equal(StallCause.SourceStalled, monitor.LastStallCause);
    }
}
