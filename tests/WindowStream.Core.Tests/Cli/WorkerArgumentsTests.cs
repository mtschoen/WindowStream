using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class WorkerArgumentsTests
{
    [Fact]
    public void Construction_PreservesAllFields()
    {
        WindowHandle hwnd = new WindowHandle(0x100);
        EncoderOptions encoderOptions = new EncoderOptions(800, 600, 30, 1_000_000, 30, 1);
        WorkerArguments arguments = new WorkerArguments(hwnd, StreamId: 7, PipeName: "test-pipe", EncoderOptions: encoderOptions);

        Assert.Equal(hwnd, arguments.Hwnd);
        Assert.Equal(7, arguments.StreamId);
        Assert.Equal("test-pipe", arguments.PipeName);
        Assert.Same(encoderOptions, arguments.EncoderOptions);
    }
}
