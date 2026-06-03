namespace WindowStream.Core.Encode;

public sealed class EncodedChunk
{
    public ReadOnlyMemory<byte> Payload { get; }
    public bool IsKeyframe { get; }
    public long PresentationTimestampMicroseconds { get; }

    public EncodedChunk(
        ReadOnlyMemory<byte> payload,
        bool isKeyframe,
        long presentationTimestampMicroseconds)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("payload must not be empty.", nameof(payload));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(presentationTimestampMicroseconds);
        Payload = payload;
        IsKeyframe = isKeyframe;
        PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
    }
}
