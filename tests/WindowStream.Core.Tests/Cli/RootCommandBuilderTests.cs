using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading.Tasks;
using WindowStream.Cli;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Testing;
using WindowStream.Core.Session.Testing;
using Xunit;

namespace WindowStream.Core.Tests.Cli;

public sealed class RootCommandBuilderTests
{
    [Fact]
    public void Builds_Root_Command_With_List_And_Serve_Subcommands()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());

        Assert.Contains(root.Children, child => child.Name == "list");
        Assert.Contains(root.Children, child => child.Name == "serve");
    }

    [Fact]
    public void Serve_Parses_Hwnd_Option_As_Long()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());
        var result = root.Parse(new[] { "serve", "--hwnd", "1234" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Serve_Parses_Title_Matches_Option_As_String()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());
        var result = root.Parse(new[] { "serve", "--title-matches", "Notepad.*" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Serve_Rejects_When_Neither_Hwnd_Nor_Title_Matches_Provided()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());
        var result = root.Parse(new[] { "serve" });
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task List_Command_Invocation_Prints_Windows()
    {
        var windows = new List<WindowInformation>
        {
            new WindowInformation(new WindowHandle(100), "Test Window", "testapp", 800, 600),
        };
        var captureSource = new FakeWindowCaptureSource(windows);
        var hostLauncher = new FakeSessionHostLauncher();
        using var output = new StringWriter();
        var services = new NamedCliServices(captureSource, hostLauncher, output);

        var root = RootCommandBuilder.Build(services);
        var exitCode = await root.InvokeAsync(new[] { "list" });

        Assert.Equal(0, exitCode);
        Assert.Contains("100", output.ToString());
    }

    [Fact]
    public async Task Serve_Command_With_Hwnd_Invocation_Launches_Session()
    {
        var windows = new List<WindowInformation>
        {
            new WindowInformation(new WindowHandle(555), "Target", "app", 800, 600),
        };
        var captureSource = new FakeWindowCaptureSource(windows);
        var hostLauncher = new FakeSessionHostLauncher();
        using var output = new StringWriter();
        var services = new NamedCliServices(captureSource, hostLauncher, output);

        var root = RootCommandBuilder.Build(services);
        var exitCode = await root.InvokeAsync(new[] { "serve", "--hwnd", "555" });

        Assert.Equal(0, exitCode);
        Assert.Equal(new WindowHandle(555), hostLauncher.LaunchedHandle);
    }

    [Fact]
    public async Task Serve_Command_With_Title_Matches_Invocation_Launches_Session()
    {
        var windows = new List<WindowInformation>
        {
            new WindowInformation(new WindowHandle(777), "My App Window", "myapp", 800, 600),
        };
        var captureSource = new FakeWindowCaptureSource(windows);
        var hostLauncher = new FakeSessionHostLauncher();
        using var output = new StringWriter();
        var services = new NamedCliServices(captureSource, hostLauncher, output);

        var root = RootCommandBuilder.Build(services);
        var exitCode = await root.InvokeAsync(new[] { "serve", "--title-matches", "My App.*" });

        Assert.Equal(0, exitCode);
        Assert.Equal(new WindowHandle(777), hostLauncher.LaunchedHandle);
    }

    [Fact]
    public async Task Serve_Command_With_Title_Matches_No_Match_Returns_Error_Exit_Code()
    {
        var captureSource = new FakeWindowCaptureSource(new List<WindowInformation>());
        var hostLauncher = new FakeSessionHostLauncher();
        using var output = new StringWriter();
        var services = new NamedCliServices(captureSource, hostLauncher, output);

        var root = RootCommandBuilder.Build(services);
        var exitCode = await root.InvokeAsync(new[] { "serve", "--title-matches", "NoMatch" });

        Assert.Equal(2, exitCode);
    }
}
