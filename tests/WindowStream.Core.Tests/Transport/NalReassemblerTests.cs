using System;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class NalReassemblerTests
{
    [Fact]
    public void SinglePacketNalIsEmittedImmediately()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1000, PacketFlags.LastFragment, FragmentIndex: 0, FragmentTotal: 1);
        ReassembledNalUnit? result = reassembler.Offer(header, new byte[] { 0x41, 0x42 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x41, 0x42 }, result!.Value.NalUnit);
        Assert.Equal((uint)1, result.Value.StreamId);
        Assert.Equal((uint)1, result.Value.Sequence);
        Assert.Equal((ulong)1000, result.Value.PresentationTimestampMicroseconds);
        Assert.False(result.Value.IsIdrFrame);
    }

    [Fact]
    public void IdrFlagPropagatesToReassembledUnit()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1, PacketFlags.IdrFrame | PacketFlags.LastFragment, 0, 1);
        ReassembledNalUnit? result = reassembler.Offer(header, new byte[] { 0x65 });
        Assert.NotNull(result);
        Assert.True(result!.Value.IsIdrFrame);
    }

    [Fact]
    public void MultiFragmentInOrderProducesConcatenation()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 3);
        PacketHeader second = new(1, 10, 100, PacketFlags.None, 1, 3);
        PacketHeader third = new(1, 10, 100, PacketFlags.LastFragment, 2, 3);
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01, 0x02 }));
        Assert.Null(reassembler.Offer(second, new byte[] { 0x03, 0x04 }));
        ReassembledNalUnit? result = reassembler.Offer(third, new byte[] { 0x05 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, result!.Value.NalUnit);
    }

    [Fact]
    public void OutOfOrderFragmentsStillReassemble()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 3);
        PacketHeader second = new(1, 10, 100, PacketFlags.None, 1, 3);
        PacketHeader third = new(1, 10, 100, PacketFlags.LastFragment, 2, 3);
        Assert.Null(reassembler.Offer(third, new byte[] { 0x05 }));
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01, 0x02 }));
        ReassembledNalUnit? result = reassembler.Offer(second, new byte[] { 0x03, 0x04 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, result!.Value.NalUnit);
    }

    [Fact]
    public void DuplicateFragmentIsIgnored()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 2);
        PacketHeader duplicate = new(1, 10, 100, PacketFlags.None, 0, 2);
        PacketHeader second = new(1, 10, 100, PacketFlags.LastFragment, 1, 2);
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01 }));
        Assert.Null(reassembler.Offer(duplicate, new byte[] { 0xFF }));  // should NOT overwrite
        ReassembledNalUnit? result = reassembler.Offer(second, new byte[] { 0x02 });
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 0x01, 0x02 }, result!.Value.NalUnit);
    }

    [Fact]
    public void DifferentStreamsAreBufferedIndependently()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader streamOneFirst = new(1, 1, 1, PacketFlags.None, 0, 2);
        PacketHeader streamOneLast = new(1, 1, 1, PacketFlags.LastFragment, 1, 2);
        PacketHeader streamTwoFirst = new(2, 1, 1, PacketFlags.None, 0, 2);
        PacketHeader streamTwoLast = new(2, 1, 1, PacketFlags.LastFragment, 1, 2);
        Assert.Null(reassembler.Offer(streamOneFirst, new byte[] { 0x11 }));
        Assert.Null(reassembler.Offer(streamTwoFirst, new byte[] { 0x22 }));
        ReassembledNalUnit? one = reassembler.Offer(streamOneLast, new byte[] { 0x1A });
        ReassembledNalUnit? two = reassembler.Offer(streamTwoLast, new byte[] { 0x2A });
        Assert.Equal(new byte[] { 0x11, 0x1A }, one!.Value.NalUnit);
        Assert.Equal(new byte[] { 0x22, 0x2A }, two!.Value.NalUnit);
    }

    [Fact]
    public void PartialAssemblyIsDiscardedAfterTimeout()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader first = new(1, 10, 100, PacketFlags.None, 0, 2);
        PacketHeader second = new(1, 10, 100, PacketFlags.LastFragment, 1, 2);
        Assert.Null(reassembler.Offer(first, new byte[] { 0x01 }));
        clock.Advance(TimeSpan.FromMilliseconds(501));
        // Second fragment arrives too late — reassembler should treat this as a fresh (incomplete) batch
        // and NOT emit anything.
        Assert.Null(reassembler.Offer(second, new byte[] { 0x02 }));
        Assert.Equal(1, reassembler.PurgeExpired());
    }

    [Fact]
    public void PurgeExpiredRemovesOnlyTimedOutEntries()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader staleFirst = new(1, 10, 100, PacketFlags.None, 0, 2);
        Assert.Null(reassembler.Offer(staleFirst, new byte[] { 0x01 }));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        PacketHeader freshFirst = new(1, 11, 100, PacketFlags.None, 0, 2);
        Assert.Null(reassembler.Offer(freshFirst, new byte[] { 0x02 }));
        clock.Advance(TimeSpan.FromMilliseconds(300));  // total 600 for sequence 10, 300 for sequence 11
        Assert.Equal(1, reassembler.PurgeExpired());
        PacketHeader freshLast = new(1, 11, 100, PacketFlags.LastFragment, 1, 2);
        ReassembledNalUnit? result = reassembler.Offer(freshLast, new byte[] { 0x03 });
        Assert.NotNull(result);
    }

    [Fact]
    public void OfferRejectsPayloadLargerThanMaximum()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1, PacketFlags.LastFragment, 0, 1);
        Assert.Throws<ArgumentException>(
            () => reassembler.Offer(header, new byte[PacketHeader.MaximumPayloadByteLength + 1]));
    }

    [Fact]
    public void OfferRejectsNullPayload()
    {
        FakeClock clock = new();
        NalReassembler reassembler = new(clock, TimeSpan.FromMilliseconds(500));
        PacketHeader header = new(1, 1, 1, PacketFlags.LastFragment, 0, 1);
        Assert.Throws<ArgumentNullException>(() => reassembler.Offer(header, null!));
    }

    [Fact]
    public void ConstructorRejectsNegativeTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NalReassembler(new FakeClock(), TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void ConstructorRejectsNullClock()
    {
        Assert.Throws<ArgumentNullException>(
            () => new NalReassembler(null!, TimeSpan.FromMilliseconds(500)));
    }
}
