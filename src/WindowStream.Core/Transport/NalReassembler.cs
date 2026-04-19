using System;
using System.Collections.Generic;

namespace WindowStream.Core.Transport;

public sealed class NalReassembler
{
    private readonly IClock clock;
    private readonly TimeSpan reassemblyTimeout;
    private readonly Dictionary<(uint StreamId, uint Sequence), FragmentBuffer> buffers = new();

    public NalReassembler(IClock clock, TimeSpan reassemblyTimeout)
    {
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }
        if (reassemblyTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reassemblyTimeout), reassemblyTimeout, "timeout must be non-negative");
        }
        this.clock = clock;
        this.reassemblyTimeout = reassemblyTimeout;
    }

    public ReassembledNalUnit? Offer(PacketHeader header, byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        if (payload.Length > PacketHeader.MaximumPayloadByteLength)
        {
            throw new ArgumentException(
                $"payload {payload.Length} bytes exceeds maximum {PacketHeader.MaximumPayloadByteLength}",
                nameof(payload));
        }

        (uint StreamId, uint Sequence) key = (header.StreamId, header.Sequence);
        DateTimeOffset now = clock.UtcNow;

        if (!buffers.TryGetValue(key, out FragmentBuffer? buffer))
        {
            buffer = new FragmentBuffer(header.FragmentTotal, header.IsIdrFrame, header.PresentationTimestampMicroseconds, now);
            buffers[key] = buffer;
        }
        else if (now - buffer.FirstSeenAt > reassemblyTimeout)
        {
            // Previous partial assembly expired — discard the late fragment without creating a new buffer.
            // PurgeExpired() will clean up the stale buffer on the next sweep.
            return null;
        }

        if (!buffer.AddFragment(header.FragmentIndex, payload))
        {
            return null;  // duplicate fragment index
        }

        if (!buffer.IsComplete)
        {
            return null;
        }

        buffers.Remove(key);
        return new ReassembledNalUnit(
            StreamId: header.StreamId,
            Sequence: header.Sequence,
            PresentationTimestampMicroseconds: buffer.PresentationTimestampMicroseconds,
            IsIdrFrame: buffer.IsIdrFrame,
            NalUnit: buffer.Concatenate());
    }

    public int PurgeExpired()
    {
        DateTimeOffset now = clock.UtcNow;
        List<(uint StreamId, uint Sequence)> expiredKeys = new();
        foreach (KeyValuePair<(uint StreamId, uint Sequence), FragmentBuffer> entry in buffers)
        {
            if (now - entry.Value.FirstSeenAt > reassemblyTimeout)
            {
                expiredKeys.Add(entry.Key);
            }
        }
        foreach ((uint StreamId, uint Sequence) expiredKey in expiredKeys)
        {
            buffers.Remove(expiredKey);
        }
        return expiredKeys.Count;
    }

    private sealed class FragmentBuffer
    {
        private readonly byte[]?[] fragments;
        private int receivedCount;

        public FragmentBuffer(int fragmentTotal, bool isIdrFrame, ulong presentationTimestampMicroseconds, DateTimeOffset firstSeenAt)
        {
            fragments = new byte[fragmentTotal][];
            IsIdrFrame = isIdrFrame;
            PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
            FirstSeenAt = firstSeenAt;
        }

        public bool IsIdrFrame { get; }
        public ulong PresentationTimestampMicroseconds { get; }
        public DateTimeOffset FirstSeenAt { get; }
        public bool IsComplete => receivedCount == fragments.Length;

        public bool AddFragment(int index, byte[] payload)
        {
            if (fragments[index] is not null)
            {
                return false;
            }
            fragments[index] = payload;
            receivedCount++;
            return true;
        }

        public byte[] Concatenate()
        {
            int totalLength = 0;
            for (int index = 0; index < fragments.Length; index++)
            {
                totalLength += fragments[index]!.Length;
            }
            byte[] result = new byte[totalLength];
            int cursor = 0;
            for (int index = 0; index < fragments.Length; index++)
            {
                byte[] fragment = fragments[index]!;
                Array.Copy(fragment, 0, result, cursor, fragment.Length);
                cursor += fragment.Length;
            }
            return result;
        }
    }
}
