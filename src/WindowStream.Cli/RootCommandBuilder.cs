using System.CommandLine;
using System.Text.Json;
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

        var serveCommand = new Command(
            "serve",
            "Start the v2 coordinator. Viewer selects windows remotely via OPEN_STREAM.");
        serveCommand.SetHandler(async invocationContext =>
        {
            var handler = new ServeCommandHandler(services.HostLauncher);
            invocationContext.ExitCode = await handler.ExecuteAsync(
                new ServeArguments(), invocationContext.GetCancellationToken());
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
            var hwnd = invocationContext.ParseResult.GetValueForOption(workerHwndOption);
            var streamId = invocationContext.ParseResult.GetValueForOption(workerStreamIdOption);
            var pipeName = invocationContext.ParseResult.GetValueForOption(workerPipeNameOption)!;
            var encoderOptionsJson = invocationContext.ParseResult.GetValueForOption(workerEncoderOptionsOption)!;

            var encoderOptions =
                JsonSerializer.Deserialize<EncoderOptions>(encoderOptionsJson)
                ?? throw new InvalidOperationException("could not parse encoder options");
            // Consumed only under #if WINDOWS (WorkerCommandHandler is Windows-only). Built on
            // every TFM so the parse/validation runs uniformly; moving it into #if WINDOWS would
            // redistribute coverage across the multi-target CLI build (gated at 100%).
            // ReSharper disable once UnusedVariable
            var arguments = new WorkerArguments(
                new WindowHandle(hwnd), streamId, pipeName, encoderOptions);

#if WINDOWS
            invocationContext.ExitCode = await WorkerCommandHandler.ExecuteAsync(arguments, invocationContext.GetCancellationToken());
#else
            await Task.CompletedTask;
            await Console.Error.WriteLineAsync("worker subcommand requires Windows").ConfigureAwait(false);
            invocationContext.ExitCode = 4;
#endif
        });
        root.AddCommand(workerCommand);

        return root;
    }
}
