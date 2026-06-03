namespace WindowStream.Core.Transport;

public static class NalFragmenter
{
    public static IEnumerable<FragmentedPacket> Fragment(
        int streamId,
        int sequence,
        long presentationTimestampMicroseconds,
        bool isIdrFrame,
        byte[] nalUnit)
    {
        ArgumentNullException.ThrowIfNull(nalUnit);
        if (nalUnit.Length == 0)
        {
            throw new ArgumentException("nalUnit must not be empty", nameof(nalUnit));
        }
        var fragmentTotal = (nalUnit.Length + PacketHeader.MaximumPayloadByteLength - 1) / PacketHeader.MaximumPayloadByteLength;
        if (fragmentTotal > 256)
        {
            throw new ArgumentException(
                $"nalUnit too large: {nalUnit.Length} bytes would require {fragmentTotal} fragments (maximum 256)",
                nameof(nalUnit));
        }
        return EnumerateFragments(streamId, sequence, presentationTimestampMicroseconds, isIdrFrame, nalUnit, fragmentTotal);
    }

    static IEnumerable<FragmentedPacket> EnumerateFragments(
        int streamId,
        int sequence,
        long presentationTimestampMicroseconds,
        bool isIdrFrame,
        byte[] nalUnit,
        int fragmentTotal)
    {
        var offset = 0;
        for (var fragmentIndex = 0; fragmentIndex < fragmentTotal; fragmentIndex++)
        {
            var fragmentLength = Math.Min(PacketHeader.MaximumPayloadByteLength, nalUnit.Length - offset);
            var isLast = fragmentIndex == fragmentTotal - 1;
            var flags = PacketFlags.None;
            if (isIdrFrame)
            {
                flags |= PacketFlags.IdrFrame;
            }
            if (isLast)
            {
                flags |= PacketFlags.LastFragment;
            }
            PacketHeader header = new(
                StreamId: streamId,
                Sequence: sequence,
                PresentationTimestampMicroseconds: presentationTimestampMicroseconds,
                Flags: flags,
                FragmentIndex: fragmentIndex,
                FragmentTotal: fragmentTotal);
            yield return new FragmentedPacket(header, new ReadOnlyMemory<byte>(nalUnit, offset, fragmentLength));
            offset += fragmentLength;
        }
    }
}
