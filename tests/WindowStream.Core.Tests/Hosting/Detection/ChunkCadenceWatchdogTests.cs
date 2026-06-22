using Microsoft.Extensions.Time.Testing;
using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Hosting.Detection;
using Xunit;

namespace WindowStream.Core.Tests.Hosting.Detection;

public sealed class ChunkCadenceWatchdogTests
{
    static ChunkCadenceWatchdog Build(FakeTimeProvider time) =>
        new(time, ChunkCadenceWatchdogOptions.Default);

    [Fact]
    public void Does_not_fire_before_any_chunk_within_startup_grace()
    {
        var time = new FakeTimeProvider();
        var watchdog = Build(time);
        time.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(StallTransition.None, watchdog.Evaluate());
    }

    [Fact]
    public void Fires_worker_silent_after_silence_floor_following_chunks()
    {
        var time = new FakeTimeProvider();
        var watchdog = Build(time);
        watchdog.RecordChunk();
        time.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.Equal(StallTransition.None, watchdog.Evaluate());
        time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal(StallTransition.Stalled, watchdog.Evaluate());
        Assert.Equal(StallCause.WorkerSilent, watchdog.LastStallCause);
    }

    [Fact]
    public void Suppressed_while_worker_reported_stalled()
    {
        var time = new FakeTimeProvider();
        var watchdog = Build(time);
        watchdog.RecordChunk();
        watchdog.SetWorkerReportedStalled(true);
        time.Advance(TimeSpan.FromMilliseconds(10000));
        Assert.Equal(StallTransition.None, watchdog.Evaluate());
    }

    [Fact]
    public void Fires_only_once_per_silence_episode()
    {
        var time = new FakeTimeProvider();
        var watchdog = Build(time);
        watchdog.RecordChunk();
        time.Advance(TimeSpan.FromMilliseconds(3500));
        Assert.Equal(StallTransition.Stalled, watchdog.Evaluate());
        time.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Equal(StallTransition.None, watchdog.Evaluate());
    }

    [Fact]
    public void Chunk_after_watchdog_stall_emits_resume()
    {
        var time = new FakeTimeProvider();
        var watchdog = Build(time);
        watchdog.RecordChunk();
        time.Advance(TimeSpan.FromMilliseconds(3500));
        Assert.Equal(StallTransition.Stalled, watchdog.Evaluate());
        time.Advance(TimeSpan.FromMilliseconds(20));
        watchdog.RecordChunk();
        Assert.Equal(StallTransition.Resumed, watchdog.Evaluate());
    }

    [Fact]
    public void Constructor_throws_on_null_time_provider()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ChunkCadenceWatchdog(null!, ChunkCadenceWatchdogOptions.Default));
    }

    [Fact]
    public void Constructor_throws_on_null_options()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ChunkCadenceWatchdog(new FakeTimeProvider(), null!));
    }
}
