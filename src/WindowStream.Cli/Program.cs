using System.CommandLine;
using WindowStream.Cli;

// Force line-flushing on Console.Out/Error when stdout/stderr is redirected to
// a file or pipe. .NET's default StreamWriter wraps redirected handles in a
// block-buffering writer, which means diagnostics from long-running commands
// (`serve`, `worker`) never reach the log file before the process is killed.
// The `AutoFlush = true` wrappers below restore line-by-line visibility.
#pragma warning disable CA2000 // ownership transferred to Console via SetOut/SetError; Console is responsible for disposal
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
#pragma warning restore CA2000

var services = CliServices.CreateDefault();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    // ReSharper disable once AccessToDisposedClosure
    // Handler only fires during the awaited InvokeAsync run, before cancellation disposes at exit.
    cancellation.Cancel();
};
var root = RootCommandBuilder.Build(services);
return await root.InvokeAsync(args);
