using System;
using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Cli;

var services = CliServices.CreateDefault();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArguments) =>
{
    eventArguments.Cancel = true;
    cancellation.Cancel();
};
var root = RootCommandBuilder.Build(services);
return await root.InvokeAsync(args);
