using System;
using WindowStream.Core.Hosting;
using Xunit;

namespace WindowStream.Core.Tests.Hosting;

public class WorkerFrameTests
{
    [Fact]
    public void WorkerChunkFrame_HoldsPayloadAndMetadata()
    {
        byte[] payload = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1F };
        WorkerChunkFrame frame = new WorkerChunkFrame(
            PresentationTimestampMicroseconds: 16_667UL,
            IsKeyframe: true,
            Payload: payload);

        Assert.Equal(16_667UL, frame.PresentationTimestampMicroseconds);
        Assert.True(frame.IsKeyframe);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public void WorkerCommandFrame_DefaultsAreEmpty()
    {
        WorkerCommandFrame frame = new WorkerCommandFrame(WorkerCommandTag.Pause);
        Assert.Equal(WorkerCommandTag.Pause, frame.Tag);
    }

    [Theory]
    [InlineData(WorkerCommandTag.Pause, (byte)0x01)]
    [InlineData(WorkerCommandTag.Resume, (byte)0x02)]
    [InlineData(WorkerCommandTag.RequestKeyframe, (byte)0x03)]
    [InlineData(WorkerCommandTag.Shutdown, (byte)0xFF)]
    public void WorkerCommandTag_HasStableWireValue(WorkerCommandTag tag, byte expected)
    {
        Assert.Equal(expected, (byte)tag);
    }
}
