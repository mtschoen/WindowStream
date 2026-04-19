using System;
using WindowStream.Core.Transport;
using Xunit;

namespace WindowStream.Core.Tests.Transport;

public sealed class PacketHeaderTests
{
    [Fact]
    public void WriteProducesExpectedByteLayout()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        PacketHeader header = new(
            StreamId: 0x01020304,
            Sequence: 0x10203040,
            PresentationTimestampMicroseconds: 0x1122334455667788,
            Flags: PacketFlags.IdrFrame | PacketFlags.LastFragment,
            FragmentIndex: 2,
            FragmentTotal: 3);
        header.WriteTo(buffer);

        // magic 'WSTR' = 0x57535452
        Assert.Equal(0x57, buffer[0]);
        Assert.Equal(0x53, buffer[1]);
        Assert.Equal(0x54, buffer[2]);
        Assert.Equal(0x52, buffer[3]);
        Assert.Equal(0x01, buffer[4]);
        Assert.Equal(0x02, buffer[5]);
        Assert.Equal(0x03, buffer[6]);
        Assert.Equal(0x04, buffer[7]);
        Assert.Equal(0x10, buffer[8]);
        Assert.Equal(0x20, buffer[9]);
        Assert.Equal(0x30, buffer[10]);
        Assert.Equal(0x40, buffer[11]);
        Assert.Equal(0x11, buffer[12]);
        Assert.Equal(0x22, buffer[13]);
        Assert.Equal(0x33, buffer[14]);
        Assert.Equal(0x44, buffer[15]);
        Assert.Equal(0x55, buffer[16]);
        Assert.Equal(0x66, buffer[17]);
        Assert.Equal(0x77, buffer[18]);
        Assert.Equal(0x88, buffer[19]);
        Assert.Equal(0x03, buffer[20]);  // flags: IDR | LAST
        Assert.Equal(0x02, buffer[21]);  // fragmentIndex
        Assert.Equal(0x03, buffer[22]);  // fragmentTotal
        Assert.Equal(0x00, buffer[23]);  // reserved
    }

    [Fact]
    public void ParseRecoversWrittenValues()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength + 5];
        PacketHeader original = new(
            StreamId: 7,
            Sequence: 9001,
            PresentationTimestampMicroseconds: 1_234_567_890,
            Flags: PacketFlags.IdrFrame,
            FragmentIndex: 0,
            FragmentTotal: 1);
        original.WriteTo(buffer);
        PacketHeader parsed = PacketHeader.Parse(buffer);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void IsIdrFrameAndIsLastFragmentReflectFlags()
    {
        PacketHeader idrOnly = MakeHeader(PacketFlags.IdrFrame, fragmentIndex: 0, fragmentTotal: 1);
        PacketHeader lastOnly = MakeHeader(PacketFlags.LastFragment, fragmentIndex: 0, fragmentTotal: 1);
        PacketHeader both = MakeHeader(PacketFlags.IdrFrame | PacketFlags.LastFragment, 0, 1);
        PacketHeader none = MakeHeader(PacketFlags.None, 0, 1);
        Assert.True(idrOnly.IsIdrFrame);
        Assert.False(idrOnly.IsLastFragment);
        Assert.False(lastOnly.IsIdrFrame);
        Assert.True(lastOnly.IsLastFragment);
        Assert.True(both.IsIdrFrame);
        Assert.True(both.IsLastFragment);
        Assert.False(none.IsIdrFrame);
        Assert.False(none.IsLastFragment);
    }

    [Fact]
    public void ParseRejectsShortBuffer()
    {
        Assert.Throws<MalformedPacketException>(
            () => PacketHeader.Parse(new byte[PacketHeader.HeaderByteLength - 1]));
    }

    [Fact]
    public void ParseRejectsWrongMagic()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        buffer[0] = 0x00; buffer[1] = 0x00; buffer[2] = 0x00; buffer[3] = 0x00;
        buffer[22] = 0x01;  // fragmentTotal must be > 0 to reach the magic check first? it's ok — magic comes first.
        Assert.Throws<MalformedPacketException>(() => PacketHeader.Parse(buffer));
    }

    [Fact]
    public void ParseRejectsZeroFragmentTotal()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 0, FragmentTotal: 1).WriteTo(buffer);
        buffer[22] = 0x00;
        Assert.Throws<MalformedPacketException>(() => PacketHeader.Parse(buffer));
    }

    [Fact]
    public void ParseRejectsFragmentIndexAtOrAboveTotal()
    {
        byte[] buffer = new byte[PacketHeader.HeaderByteLength];
        new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 0, FragmentTotal: 1).WriteTo(buffer);
        buffer[21] = 0x05;  // index
        buffer[22] = 0x03;  // total
        Assert.Throws<MalformedPacketException>(() => PacketHeader.Parse(buffer));
    }

    [Fact]
    public void WriteRejectsShortBuffer()
    {
        PacketHeader header = MakeHeader(PacketFlags.None, 0, 1);
        Assert.Throws<ArgumentException>(() => header.WriteTo(new byte[PacketHeader.HeaderByteLength - 1]));
    }

    [Fact]
    public void ConstructorRejectsFragmentIndexAtOrAboveTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 3, FragmentTotal: 3));
    }

    [Fact]
    public void ConstructorRejectsZeroFragmentTotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PacketHeader(1, 1, 1, PacketFlags.None, FragmentIndex: 0, FragmentTotal: 0));
    }

    private static PacketHeader MakeHeader(PacketFlags flags, int fragmentIndex, int fragmentTotal)
    {
        return new PacketHeader(
            StreamId: 1,
            Sequence: 1,
            PresentationTimestampMicroseconds: 1,
            Flags: flags,
            FragmentIndex: fragmentIndex,
            FragmentTotal: fragmentTotal);
    }
}
