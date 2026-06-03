using Serilog.Events;
using Serilog.Parsing;
using WindowStream.Core.Observability;
using WindowStream.Server.Observability;
using Xunit;

namespace WindowStream.Server.Tests.Observability;

public sealed class InAppDashboardSinkTests
{
    [Fact]
    public void Emit_Adds_Entry_To_Snapshot()
    {
        var sink = new InAppDashboardSink();

        sink.Emit(MakeEvent(LogEventLevel.Information, "hello"));

        var snapshot = sink.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(Severity.Info, snapshot[0].Severity);
    }

    [Fact]
    public void Ring_Buffer_Evicts_Oldest_Past_Capacity()
    {
        var sink = new InAppDashboardSink(capacity: 3);

        for (var i = 0; i < 5; i++)
        {
            sink.Emit(MakeEvent(LogEventLevel.Information, $"message {i}"));
        }

        var snapshot = sink.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal("message 2", snapshot[0].Message);
        Assert.Equal("message 3", snapshot[1].Message);
        Assert.Equal("message 4", snapshot[2].Message);
    }

    [Fact]
    public void OnEvent_Fires_Once_Per_Emit_From_Concurrent_Threads()
    {
        var sink = new InAppDashboardSink();
        var fireCount = 0;
        sink.OnEvent += _ => Interlocked.Increment(ref fireCount);

        const int threadCount = 8;
        const int emitsPerThread = 50;
        var threads = new Thread[threadCount];

        for (var i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                for (var j = 0; j < emitsPerThread; j++)
                {
                    sink.Emit(MakeEvent(LogEventLevel.Information, "concurrent"));
                }
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Equal(threadCount * emitsPerThread, fireCount);
    }

    static LogEvent MakeEvent(LogEventLevel level, string message) =>
        new(DateTimeOffset.UtcNow, level, null,
            new MessageTemplate(message, new List<MessageTemplateToken> { new TextToken(message) }),
            new List<LogEventProperty>());
}
