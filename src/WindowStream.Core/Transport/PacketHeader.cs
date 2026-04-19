using System;
using System.Buffers.Binary;

namespace WindowStream.Core.Transport;

public readonly record struct PacketHeader(
    uint StreamId,
    uint Sequence,
    ulong PresentationTimestampMicroseconds,
    PacketFlags Flags,
    byte FragmentIndex,
    byte FragmentTotal)
{
    public const int HeaderByteLength = 24;
    public const int MaximumPayloadByteLength = 1200;
    public const uint MagicValue = 0x57535452; // 'WSTR'

    public PacketHeader(
        int StreamId,
        int Sequence,
        long PresentationTimestampMicroseconds,
        PacketFlags Flags,
        int FragmentIndex,
        int FragmentTotal)
        : this(
            checked((uint)StreamId),
            checked((uint)Sequence),
            checked((ulong)PresentationTimestampMicroseconds),
            Flags,
            checked((byte)FragmentIndex),
            checked((byte)FragmentTotal))
    {
        if (FragmentTotal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FragmentTotal), FragmentTotal, "fragmentTotal must be at least 1");
        }
        if ((uint)FragmentIndex >= (uint)FragmentTotal)
        {
            throw new ArgumentOutOfRangeException(nameof(FragmentIndex), FragmentIndex, $"fragmentIndex must be less than fragmentTotal {FragmentTotal}");
        }
    }

    public bool IsIdrFrame => (Flags & PacketFlags.IdrFrame) != 0;
    public bool IsLastFragment => (Flags & PacketFlags.LastFragment) != 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderByteLength)
        {
            throw new ArgumentException(
                $"destination must be at least {HeaderByteLength} bytes, got {destination.Length}",
                nameof(destination));
        }
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], MagicValue);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], StreamId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(destination[12..20], PresentationTimestampMicroseconds);
        destination[20] = (byte)Flags;
        destination[21] = FragmentIndex;
        destination[22] = FragmentTotal;
        destination[23] = 0x00;
    }

    public static PacketHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderByteLength)
        {
            throw new MalformedPacketException(
                $"packet is {source.Length} bytes, minimum is {HeaderByteLength}");
        }
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(source[0..4]);
        if (magic != MagicValue)
        {
            throw new MalformedPacketException($"unexpected magic: 0x{magic:X8}");
        }
        uint streamId = BinaryPrimitives.ReadUInt32BigEndian(source[4..8]);
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(source[8..12]);
        ulong presentationTimestamp = BinaryPrimitives.ReadUInt64BigEndian(source[12..20]);
        byte flags = source[20];
        byte fragmentIndex = source[21];
        byte fragmentTotal = source[22];
        if (fragmentTotal == 0)
        {
            throw new MalformedPacketException("fragmentTotal must be at least 1");
        }
        if (fragmentIndex >= fragmentTotal)
        {
            throw new MalformedPacketException(
                $"fragmentIndex {fragmentIndex} is not less than fragmentTotal {fragmentTotal}");
        }
        return new PacketHeader(
            StreamId: streamId,
            Sequence: sequence,
            PresentationTimestampMicroseconds: presentationTimestamp,
            Flags: (PacketFlags)flags,
            FragmentIndex: fragmentIndex,
            FragmentTotal: fragmentTotal);
    }
}
