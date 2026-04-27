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
    public void Serve_Parses_With_No_Arguments()
    {
        var root = RootCommandBuilder.Build(new FakeCliServices());
        var result = root.Parse(new[] { "serve" });
        Assert.Empty(result.Errors);
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
    public async Task Serve_Command_Invocation_Launches_Coordinator()
    {
        var captureSource = new FakeWindowCaptureSource(new List<WindowInformation>());
        var hostLauncher = new FakeSessionHostLauncher();
        using var output = new StringWriter();
        var services = new NamedCliServices(captureSource, hostLauncher, output);

        var root = RootCommandBuilder.Build(services);
        var exitCode = await root.InvokeAsync(new[] { "serve" });

        Assert.Equal(0, exitCode);
        Assert.True(hostLauncher.Launched);
    }
}
