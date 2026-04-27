using System;
using System.CommandLine;
using System.Text.Json;
using System.Threading.Tasks;
using WindowStream.Cli.Commands;
using WindowStream.Core.Capture;
using WindowStream.Core.Encode;

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

        var workerHwndOption = new Option<long>("--hwnd", "HWND of window to capture") { IsRequired = true };
        var workerStreamIdOption = new Option<int>("--stream-id", "Stream identifier assigned by coordinator") { IsRequired = true };
        var workerPipeNameOption = new Option<string>("--pipe-name", "Name of the coordinator's NamedPipeServerStream") { IsRequired = true };
        var workerEncoderOptionsOption = new Option<string>("--encoder-options", "JSON-serialized EncoderOptions") { IsRequired = true };

        var workerCommand = new Command("worker", "Internal: capture+encode pump for a single stream. Spawned by the coordinator.")
        {
            workerHwndOption,
            workerStreamIdOption,
            workerPipeNameOption,
            workerEncoderOptionsOption
        };
        workerCommand.SetHandler(async invocationContext =>
        {
            long hwnd = invocationContext.ParseResult.GetValueForOption(workerHwndOption);
            int streamId = invocationContext.ParseResult.GetValueForOption(workerStreamIdOption);
            string pipeName = invocationContext.ParseResult.GetValueForOption(workerPipeNameOption)!;
            string encoderOptionsJson = invocationContext.ParseResult.GetValueForOption(workerEncoderOptionsOption)!;

            EncoderOptions encoderOptions =
                JsonSerializer.Deserialize<EncoderOptions>(encoderOptionsJson)
                ?? throw new InvalidOperationException("could not parse encoder options");
            WorkerArguments arguments = new WorkerArguments(
                new WindowHandle(hwnd), streamId, pipeName, encoderOptions);

#if WINDOWS
            WorkerCommandHandler handler = new WorkerCommandHandler();
            invocationContext.ExitCode = await handler.ExecuteAsync(arguments, invocationContext.GetCancellationToken());
#else
            await Task.CompletedTask;
            Console.Error.WriteLine("worker subcommand requires Windows");
            invocationContext.ExitCode = 4;
#endif
        });
        root.AddCommand(workerCommand);

        return root;
    }
}
