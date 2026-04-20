using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class ListWindowsCommandTests
{
    [Fact]
    public async Task Prints_Table_With_Handle_Title_And_Process_Name()
    {
        var windows = new List<WindowInformation>
        {
            new WindowInformation(new WindowHandle(42), "Notepad - Untitled", "notepad", 640, 480),
            new WindowInformation(new WindowHandle(43), "WindowStream CLI", "dotnet", 800, 600),
        };
        var captureSource = new FakeWindowCaptureSource(windows);

        using var writer = new StringWriter();
        var handler = new ListWindowsCommandHandler(captureSource, writer);

        var exitCode = await handler.ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = writer.ToString();
        Assert.Contains("42", output);
        Assert.Contains("Notepad - Untitled", output);
        Assert.Contains("notepad", output);
        Assert.Contains("43", output);
    }

    [Fact]
    public async Task Prints_Header_Row()
    {
        var captureSource = new FakeWindowCaptureSource(new List<WindowInformation>());
        using var writer = new StringWriter();
        var handler = new ListWindowsCommandHandler(captureSource, writer);

        await handler.ExecuteAsync(CancellationToken.None);

        var output = writer.ToString();
        Assert.Contains("HANDLE", output);
        Assert.Contains("PROCESS", output);
        Assert.Contains("TITLE", output);
    }
}
