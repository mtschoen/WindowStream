namespace WindowStream.Core.Transport;

public sealed class NalReassembler
{
    readonly IClock _clock;
    readonly TimeSpan _reassemblyTimeout;
    readonly Dictionary<(uint StreamId, uint Sequence), FragmentBuffer> _buffers = new();

    public NalReassembler(IClock clock, TimeSpan reassemblyTimeout)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (reassemblyTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reassemblyTimeout), reassemblyTimeout, "timeout must be non-negative");
        }
        _clock = clock;
        _reassemblyTimeout = reassemblyTimeout;
    }

    public ReassembledNalUnit? Offer(PacketHeader header, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > PacketHeader.MaximumPayloadByteLength)
        {
            throw new ArgumentException(
                $"payload {payload.Length} bytes exceeds maximum {PacketHeader.MaximumPayloadByteLength}",
                nameof(payload));
        }

        var key = (header.StreamId, header.Sequence);
        var now = _clock.UtcNow;

        if (!_buffers.TryGetValue(key, out var buffer))
        {
            buffer = new FragmentBuffer(header.FragmentTotal, header.IsIdrFrame, header.PresentationTimestampMicroseconds, now);
            _buffers[key] = buffer;
        }
        else if (now - buffer.FirstSeenAt > _reassemblyTimeout)
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

        _buffers.Remove(key);
        return new ReassembledNalUnit(
            StreamId: header.StreamId,
            Sequence: header.Sequence,
            PresentationTimestampMicroseconds: buffer.PresentationTimestampMicroseconds,
            IsIdrFrame: buffer.IsIdrFrame,
            NalUnit: buffer.Concatenate());
    }

    public int PurgeExpired()
    {
        var now = _clock.UtcNow;
        List<(uint StreamId, uint Sequence)> expiredKeys = new();
        foreach (var entry in _buffers)
        {
            if (now - entry.Value.FirstSeenAt > _reassemblyTimeout)
            {
                expiredKeys.Add(entry.Key);
            }
        }
        foreach (var expiredKey in expiredKeys)
        {
            _buffers.Remove(expiredKey);
        }
        return expiredKeys.Count;
    }

    sealed class FragmentBuffer
    {
        readonly byte[]?[] _fragments;
        int _receivedCount;

        public FragmentBuffer(int fragmentTotal, bool isIdrFrame, ulong presentationTimestampMicroseconds, DateTimeOffset firstSeenAt)
        {
            _fragments = new byte[fragmentTotal][];
            IsIdrFrame = isIdrFrame;
            PresentationTimestampMicroseconds = presentationTimestampMicroseconds;
            FirstSeenAt = firstSeenAt;
        }

        public bool IsIdrFrame { get; }
        public ulong PresentationTimestampMicroseconds { get; }
        public DateTimeOffset FirstSeenAt { get; }
        public bool IsComplete => _receivedCount == _fragments.Length;

        public bool AddFragment(int index, byte[] payload)
        {
            if (_fragments[index] is not null)
            {
                return false;
            }
            _fragments[index] = payload;
            _receivedCount++;
            return true;
        }

        public byte[] Concatenate()
        {
            var totalLength = 0;
            for (var index = 0; index < _fragments.Length; index++)
            {
                totalLength += _fragments[index]!.Length;
            }
            var result = new byte[totalLength];
            var cursor = 0;
            for (var index = 0; index < _fragments.Length; index++)
            {
                var fragment = _fragments[index]!;
                Array.Copy(fragment, 0, result, cursor, fragment.Length);
                cursor += fragment.Length;
            }
            return result;
        }
    }
}
