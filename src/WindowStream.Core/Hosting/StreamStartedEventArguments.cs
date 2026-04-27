using System;
using System.IO;

namespace WindowStream.Core.Hosting;

public sealed class StreamStartedEventArguments : EventArgs
{
    public StreamStartedEventArguments(int streamId, ulong windowId, Stream pipe)
    {
        StreamId = streamId;
        WindowId = windowId;
        Pipe = pipe;
    }

    public int StreamId { get; }

    public ulong WindowId { get; }

    public Stream Pipe { get; }
}
