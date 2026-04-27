using System;
using WindowStream.Core.Protocol;

namespace WindowStream.Core.Hosting;

public sealed class StreamEndedEventArguments : EventArgs
{
    public StreamEndedEventArguments(int streamId, StreamStoppedReason reason)
    {
        StreamId = streamId;
        Reason = reason;
    }

    public int StreamId { get; }

    public StreamStoppedReason Reason { get; }
}
