using System;

namespace WindowStream.Core.Encode;

public sealed class EncodedChunk
{
    public ReadOnlyMemory<byte> payload { get; }
    public bool isKeyframe { get; }
    public long presentationTimestampMicroseconds { get; }

    public EncodedChunk(
        ReadOnlyMemory<byte> payload,
        bool isKeyframe,
        long presentationTimestampMicroseconds)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("payload must not be empty.", nameof(payload));
        }
        if (presentationTimestampMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationTimestampMicroseconds));
        }
        this.payload = payload;
        this.isKeyframe = isKeyframe;
        this.presentationTimestampMicroseconds = presentationTimestampMicroseconds;
    }
}
