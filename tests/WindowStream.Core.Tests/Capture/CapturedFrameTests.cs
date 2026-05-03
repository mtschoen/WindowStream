using System;
using Xunit;

namespace WindowStream.Core.Tests.Capture;

public sealed class CapturedFrameTests
{
    [Fact]
    public void Constructor_PopulatesAllProperties()
    {
        byte[] buffer = new byte[3 * 2 * 4];
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            widthPixels: 3,
            heightPixels: 2,
            rowStrideBytes: 12,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 1_234_567,
            pixelBuffer: buffer);

        Assert.Equal(3, frame.widthPixels);
        Assert.Equal(2, frame.heightPixels);
        Assert.Equal(12, frame.rowStrideBytes);
        Assert.Equal(WindowStream.Core.Capture.PixelFormat.Bgra32, frame.pixelFormat);
        Assert.Equal(1_234_567L, frame.presentationTimestampMicroseconds);
        Assert.Equal(buffer.Length, frame.pixelBuffer.Length);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                0, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                1, 0, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_RejectsStrideSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                10, 2, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[80]));
    }

    [Fact]
    public void Constructor_RejectsBufferTooSmall()
    {
        Assert.Throws<ArgumentException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                10, 2, 40, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]));
    }

    [Fact]
    public void Constructor_AllowsZeroTimestamp()
    {
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]);
        Assert.Equal(0L, frame.presentationTimestampMicroseconds);
    }

    [Fact]
    public void Constructor_RejectsNegativeTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowStream.Core.Capture.CapturedFrame(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, -1, new byte[4]));
    }

    [Fact]
    public void Constructor_BytesPath_SetsRepresentationToBytes()
    {
        WindowStream.Core.Capture.CapturedFrame frame = new WindowStream.Core.Capture.CapturedFrame(
            1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, new byte[4]);
        Assert.Equal(WindowStream.Core.Capture.FrameRepresentation.Bytes, frame.representation);
        Assert.Equal((nint)0, frame.nativeTexturePointer);
        Assert.Equal(0, frame.textureArrayIndex);
    }

    [Fact]
    public void FromBytes_IsEquivalentToConstructor()
    {
        byte[] buffer = new byte[8];
        WindowStream.Core.Capture.CapturedFrame frame = WindowStream.Core.Capture.CapturedFrame.FromBytes(
            widthPixels: 2,
            heightPixels: 1,
            rowStrideBytes: 8,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Bgra32,
            presentationTimestampMicroseconds: 42,
            pixelBuffer: buffer);
        Assert.Equal(WindowStream.Core.Capture.FrameRepresentation.Bytes, frame.representation);
        Assert.Equal(buffer.Length, frame.pixelBuffer.Length);
        Assert.Equal(42L, frame.presentationTimestampMicroseconds);
    }

    [Fact]
    public void FromTexture_PopulatesAllProperties()
    {
        WindowStream.Core.Capture.CapturedFrame frame = WindowStream.Core.Capture.CapturedFrame.FromTexture(
            widthPixels: 1920,
            heightPixels: 1080,
            rowStrideBytes: 1920,
            pixelFormat: WindowStream.Core.Capture.PixelFormat.Nv12,
            presentationTimestampMicroseconds: 1_000_000,
            nativeTexturePointer: (nint)0x12345678,
            textureArrayIndex: 3);

        Assert.Equal(WindowStream.Core.Capture.FrameRepresentation.Texture, frame.representation);
        Assert.Equal(1920, frame.widthPixels);
        Assert.Equal(1080, frame.heightPixels);
        Assert.Equal(1920, frame.rowStrideBytes);
        Assert.Equal(WindowStream.Core.Capture.PixelFormat.Nv12, frame.pixelFormat);
        Assert.Equal(1_000_000L, frame.presentationTimestampMicroseconds);
        Assert.Equal((nint)0x12345678, frame.nativeTexturePointer);
        Assert.Equal(3, frame.textureArrayIndex);
        Assert.Equal(0, frame.pixelBuffer.Length);
    }

    [Fact]
    public void FromTexture_RejectsZeroPointer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)0, 0));
    }

    [Fact]
    public void FromTexture_RejectsNegativeArrayIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, -1));
    }

    [Fact]
    public void FromTexture_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                0, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 0, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, 0));
    }

    [Fact]
    public void FromTexture_RejectsStrideSmallerThanRow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                10, 2, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, 0, (nint)1, 0));
    }

    [Fact]
    public void FromTexture_RejectsNegativeTimestamp()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowStream.Core.Capture.CapturedFrame.FromTexture(
                1, 1, 4, WindowStream.Core.Capture.PixelFormat.Bgra32, -1, (nint)1, 0));
    }
}
