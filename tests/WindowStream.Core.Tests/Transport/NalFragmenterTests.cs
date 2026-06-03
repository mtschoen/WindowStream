using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class NalFragmenterTests
{
    [Fact]
    public void SmallNalProducesSinglePacketWithLastFlag()
    {
        var nalUnit = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42 }; // SPS-ish
        List<FragmentedPacket> packets = new(NalFragmenter.Fragment(
            streamId: 7,
            sequence: 100,
            presentationTimestampMicroseconds: 1000,
            isIdrFrame: false,
            nalUnit: nalUnit));
        Assert.Single(packets);
        Assert.Equal((byte)0, packets[0].Header.FragmentIndex);
        Assert.Equal((byte)1, packets[0].Header.FragmentTotal);
        Assert.True(packets[0].Header.IsLastFragment);
        Assert.False(packets[0].Header.IsIdrFrame);
        Assert.Equal(nalUnit, packets[0].Payload.ToArray());
    }

    [Fact]
    public void IdrFlagIsSetOnEveryFragmentWhenRequested()
    {
        var nalUnit = new byte[PacketHeader.MaximumPayloadByteLength * 2 + 100];
#pragma warning disable CA5394 // non-cryptographic: seeded Random used only to fill test payload bytes
        new Random(42).NextBytes(nalUnit);
#pragma warning restore CA5394
        List<FragmentedPacket> packets = new(NalFragmenter.Fragment(
            streamId: 1, sequence: 1, presentationTimestampMicroseconds: 1,
            isIdrFrame: true, nalUnit: nalUnit));
        Assert.Equal(3, packets.Count);
        foreach (var packet in packets)
        {
            Assert.True(packet.Header.IsIdrFrame);
        }
        Assert.False(packets[0].Header.IsLastFragment);
        Assert.False(packets[1].Header.IsLastFragment);
        Assert.True(packets[2].Header.IsLastFragment);
    }

    [Fact]
    public void FragmentIndicesAreContiguousAndTotalMatches()
    {
        var nalUnit = new byte[PacketHeader.MaximumPayloadByteLength * 3];
        List<FragmentedPacket> packets = new(NalFragmenter.Fragment(1, 1, 1, false, nalUnit));
        Assert.Equal(3, packets.Count);
        for (var index = 0; index < packets.Count; index++)
        {
            Assert.Equal((byte)index, packets[index].Header.FragmentIndex);
            Assert.Equal((byte)3, packets[index].Header.FragmentTotal);
        }
    }

    [Fact]
    public void PayloadIsConcatenationOfAllFragmentsInOrder()
    {
        var nalUnit = new byte[PacketHeader.MaximumPayloadByteLength + 500];
        for (var index = 0; index < nalUnit.Length; index++)
        {
            nalUnit[index] = (byte)(index & 0xFF);
        }
        List<FragmentedPacket> packets = new(NalFragmenter.Fragment(1, 1, 1, false, nalUnit));
        var reassembled = new byte[nalUnit.Length];
        var cursor = 0;
        foreach (var packet in packets)
        {
            packet.Payload.Span.CopyTo(reassembled.AsSpan(cursor));
            cursor += packet.Payload.Length;
        }
        Assert.Equal(nalUnit, reassembled);
    }

    [Fact]
    public void EmptyNalUnitThrows()
    {
        Assert.Throws<ArgumentException>(
            () =>
            {
                foreach (var _ in NalFragmenter.Fragment(1, 1, 1, false, Array.Empty<byte>())) { }
            });
    }

    [Fact]
    public void NullNalUnitThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () =>
            {
                foreach (var _ in NalFragmenter.Fragment(1, 1, 1, false, null!)) { }
            });
    }

    [Fact]
    public void TooManyFragmentsThrows()
    {
        // 256 fragments is the byte-size limit on fragmentTotal; exactly 256 should pass, 257 should fail.
        var nalUnit = new byte[PacketHeader.MaximumPayloadByteLength * 256 + 1];
        Assert.Throws<ArgumentException>(
            () =>
            {
                foreach (var _ in NalFragmenter.Fragment(1, 1, 1, false, nalUnit)) { }
            });
    }

    [Fact]
    public void HeaderStreamIdAndSequenceArePropagated()
    {
        var nalUnit = new byte[100];
        List<FragmentedPacket> packets = new(NalFragmenter.Fragment(
            streamId: 42, sequence: 99, presentationTimestampMicroseconds: 1234, false, nalUnit));
        Assert.Equal((uint)42, packets[0].Header.StreamId);
        Assert.Equal((uint)99, packets[0].Header.Sequence);
        Assert.Equal((ulong)1234, packets[0].Header.PresentationTimestampMicroseconds);
    }
}
