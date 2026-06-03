namespace WindowStream.Core.Hosting;

public sealed class StreamStartedEventArguments : EventArgs
{
    public StreamStartedEventArguments(int streamId, ulong windowId, Stream pipe, int workerProcessId)
    {
        StreamId = streamId;
        WindowId = windowId;
        Pipe = pipe;
        WorkerProcessId = workerProcessId;
    }

    public int StreamId { get; }

    public ulong WindowId { get; }

    public Stream Pipe { get; }

    public int WorkerProcessId { get; }
}
