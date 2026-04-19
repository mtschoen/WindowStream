namespace WindowStream.Core.Transport;

public readonly record struct ReassembledNalUnit(
    uint StreamId,
    uint Sequence,
    ulong PresentationTimestampMicroseconds,
    bool IsIdrFrame,
    byte[] NalUnit);
