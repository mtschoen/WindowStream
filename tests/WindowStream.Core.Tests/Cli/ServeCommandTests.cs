using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class ServeCommandTests
{
    [Fact]
    public async Task Serve_With_Hwnd_Starts_Session_On_Specified_Handle()
    {
        var windows = new List<WindowInformation>
        {
            new WindowInformation(new WindowHandle(1001), "Target", "notepad", 640, 480),
        };
        var captureSource = new FakeWindowCaptureSource(windows);
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(captureSource, hostLauncher);

        using var cancellation = new CancellationTokenSource();
        _ = Task.Run(async () => { await Task.Delay(50); cancellation.Cancel(); });

        var exitCode = await handler.ExecuteAsync(new ServeArguments(Handle: new WindowHandle(1001), TitlePattern: null), cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(new WindowHandle(1001), hostLauncher.LaunchedHandle);
    }

    [Fact]
    public async Task Serve_With_Title_Matches_Finds_First_Visible_Window()
    {
        var windows = new List<WindowInformation>
        {
            new WindowInformation(new WindowHandle(7), "Chrome", "chrome", 1920, 1080),
            new WindowInformation(new WindowHandle(8), "Notepad - README", "notepad", 800, 600),
        };
        var captureSource = new FakeWindowCaptureSource(windows);
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(captureSource, hostLauncher);

        using var cancellation = new CancellationTokenSource();
        _ = Task.Run(async () => { await Task.Delay(50); cancellation.Cancel(); });

        var exitCode = await handler.ExecuteAsync(new ServeArguments(Handle: null, TitlePattern: "Notepad.*"), cancellation.Token);

        Assert.Equal(0, exitCode);
        Assert.Equal(new WindowHandle(8), hostLauncher.LaunchedHandle);
    }

    [Fact]
    public async Task Serve_With_Title_Matches_Returns_Error_When_No_Match()
    {
        var windows = new List<WindowInformation>
        {
            new WindowInformation(new WindowHandle(7), "Chrome", "chrome", 1920, 1080),
        };
        var captureSource = new FakeWindowCaptureSource(windows);
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(captureSource, hostLauncher);

        var exitCode = await handler.ExecuteAsync(new ServeArguments(Handle: null, TitlePattern: "Nope"), CancellationToken.None);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Serve_Returns_Error_When_Neither_Handle_Nor_Pattern_Provided()
    {
        var captureSource = new FakeWindowCaptureSource(new List<WindowInformation>());
        var hostLauncher = new FakeSessionHostLauncher();
        var handler = new ServeCommandHandler(captureSource, hostLauncher);

        var exitCode = await handler.ExecuteAsync(new ServeArguments(Handle: null, TitlePattern: null), CancellationToken.None);

        Assert.Equal(2, exitCode);
    }
}
