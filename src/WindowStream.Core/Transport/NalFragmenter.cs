using System;
using System.Collections.Generic;

namespace WindowStream.Core.Transport;

public sealed class NalFragmenter
{
    public IEnumerable<FragmentedPacket> Fragment(
        int streamId,
        int sequence,
        long presentationTimestampMicroseconds,
        bool isIdrFrame,
        byte[] nalUnit)
    {
        if (nalUnit is null)
        {
            throw new ArgumentNullException(nameof(nalUnit));
        }
        if (nalUnit.Length == 0)
        {
            throw new ArgumentException("nalUnit must not be empty", nameof(nalUnit));
        }
        int fragmentTotal = (nalUnit.Length + PacketHeader.MaximumPayloadByteLength - 1) / PacketHeader.MaximumPayloadByteLength;
        if (fragmentTotal > 256)
        {
            throw new ArgumentException(
                $"nalUnit too large: {nalUnit.Length} bytes would require {fragmentTotal} fragments (maximum 256)",
                nameof(nalUnit));
        }
        return EnumerateFragments(streamId, sequence, presentationTimestampMicroseconds, isIdrFrame, nalUnit, fragmentTotal);
    }

    private static IEnumerable<FragmentedPacket> EnumerateFragments(
        int streamId,
        int sequence,
        long presentationTimestampMicroseconds,
        bool isIdrFrame,
        byte[] nalUnit,
        int fragmentTotal)
    {
        int offset = 0;
        for (int fragmentIndex = 0; fragmentIndex < fragmentTotal; fragmentIndex++)
        {
            int fragmentLength = Math.Min(PacketHeader.MaximumPayloadByteLength, nalUnit.Length - offset);
            bool isLast = fragmentIndex == fragmentTotal - 1;
            PacketFlags flags = PacketFlags.None;
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
