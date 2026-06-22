using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Observability;
using Xunit;

namespace WindowStream.Core.Tests.Observability;

public sealed class SourceStallEventTests
{
    [Fact]
    public void SourceStalled_is_warning_and_carries_fields()
    {
        var stalled = new PipelineEvent.SourceStalled(3, StallCause.SourceStalled, 250);
        Assert.Equal(Severity.Warning, stalled.Severity);
        Assert.Equal(3, stalled.StreamId);
        Assert.Equal(StallCause.SourceStalled, stalled.Cause);
        Assert.Equal(250, stalled.LastFrameAgeMilliseconds);
    }

    [Fact]
    public void SourceResumed_is_info()
    {
        var resumed = new PipelineEvent.SourceResumed(3, 1200);
        Assert.Equal(Severity.Info, resumed.Severity);
        Assert.Equal(1200, resumed.StalledForMilliseconds);
    }

    [Fact]
    public void Worker_error_events_are_warning()
    {
        Assert.Equal(Severity.Warning, new PipelineEvent.CaptureErrorReported(1, "boom").Severity);
        Assert.Equal(Severity.Warning, new PipelineEvent.EncodeErrorReported(1, "boom").Severity);
    }
}
