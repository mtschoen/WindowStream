using System.CommandLine;
using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;

namespace WindowStream.Cli;

public static class RootCommandBuilder
{
    public static RootCommand Build(ICliServices services)
    {
        var root = new RootCommand("WindowStream CLI");

        var listCommand = new Command("list", "Enumerate streamable windows");
        listCommand.SetHandler(async invocationContext =>
        {
            var handler = new ListWindowsCommandHandler(services.CaptureSource, services.Output);
            invocationContext.ExitCode = await handler.ExecuteAsync(invocationContext.GetCancellationToken());
        });
        root.AddCommand(listCommand);

        var handleOption = new Option<long?>("--hwnd", "HWND of window to stream");
        var titleOption = new Option<string?>("--title-matches", "Regex matched against window titles");
        var serveCommand = new Command("serve", "Start a streaming session")
        {
            handleOption,
            titleOption
        };
        serveCommand.AddValidator(result =>
        {
            if (result.GetValueForOption(handleOption) is null && string.IsNullOrEmpty(result.GetValueForOption(titleOption)))
            {
                result.ErrorMessage = "Provide --hwnd or --title-matches";
            }
        });
        serveCommand.SetHandler(async invocationContext =>
        {
            var rawHandle = invocationContext.ParseResult.GetValueForOption(handleOption);
            var titlePattern = invocationContext.ParseResult.GetValueForOption(titleOption);
            var arguments = new ServeArguments(
                Handle: rawHandle is null ? null : new WindowHandle(rawHandle.Value),
                TitlePattern: titlePattern);
            var handler = new ServeCommandHandler(services.CaptureSource, services.HostLauncher);
            invocationContext.ExitCode = await handler.ExecuteAsync(arguments, invocationContext.GetCancellationToken());
        });
        root.AddCommand(serveCommand);

        return root;
    }
}
