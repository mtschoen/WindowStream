using WindowStream.Core.Observability;
using WindowStream.Server.Observability;
using Xunit;

namespace WindowStream.Server.Tests.Observability;

public class ServerStateReducerTests
{
    [Fact]
    public void Initial_State_Is_All_Pending()
    {
        ServerStateReducer reducer = new();
        Assert.Equal(StageStatus.Pending, reducer.State.Listening);
        Assert.Equal(StageStatus.Pending, reducer.State.ViewerConnected);
        Assert.Empty(reducer.State.Streams);
    }

    [Fact]
    public void Listening_Event_Sets_Listening_Ok_And_Ports()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.Listening(53234, 53235));
        Assert.Equal(StageStatus.Ok, reducer.State.Listening);
        Assert.Equal(53234, reducer.State.TcpPort);
        Assert.Equal(53235, reducer.State.UdpPort);
    }

    [Fact]
    public void OpenStreamReceived_Creates_New_Stream_Row_With_Pending_Stages()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.OpenStreamReceived(StreamId: 1, WindowId: 7));
        var row = reducer.State.Streams[1];
        Assert.Equal(7UL, row.WindowId);
        Assert.Equal(StageStatus.Pending, row.WorkerSpawn);
    }

    [Fact]
    public void WorkerSpawnFailed_Transitions_Row_To_Error()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.OpenStreamReceived(1, 7));
        reducer.Apply(new PipelineEvent.WorkerSpawnFailed(1, new InvalidOperationException("boom")));
        Assert.Equal(StageStatus.Error, reducer.State.Streams[1].WorkerSpawn);
        Assert.Equal("boom", reducer.State.Streams[1].WorkerSpawnError);
    }

    [Fact]
    public void StreamStopped_Removes_Row()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.OpenStreamReceived(1, 7));
        reducer.Apply(new PipelineEvent.StreamStopped(1, "viewer-disconnect"));
        Assert.False(reducer.State.Streams.ContainsKey(1));
    }

    [Fact]
    public void WindowAppeared_And_Disappeared_Adjust_Window_Count()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.WindowAppeared(WindowId: 1UL, Title: "a", ProcessName: "p", Width: 100, Height: 100));
        reducer.Apply(new PipelineEvent.WindowAppeared(WindowId: 2UL, Title: "b", ProcessName: "p", Width: 100, Height: 100));
        Assert.Equal(2, reducer.State.WindowCount);
        reducer.Apply(new PipelineEvent.WindowDisappeared(WindowId: 1UL));
        Assert.Equal(1, reducer.State.WindowCount);
    }

    [Fact]
    public void WindowDisappeared_Below_Zero_Is_Clamped()
    {
        ServerStateReducer reducer = new();
        reducer.Apply(new PipelineEvent.WindowDisappeared(WindowId: 99UL));
        Assert.Equal(0, reducer.State.WindowCount);
    }
}
