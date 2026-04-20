using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;

namespace WindowStream.Cli.Commands;

public sealed class ListWindowsCommandHandler
{
    private readonly IWindowCaptureSource captureSource;
    private readonly TextWriter writer;

    public ListWindowsCommandHandler(IWindowCaptureSource captureSource, TextWriter writer)
    {
        this.captureSource = captureSource;
        this.writer = writer;
    }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        writer.WriteLine($"{"HANDLE",-12} {"PROCESS",-20} TITLE");
        foreach (var window in captureSource.ListWindows())
        {
            writer.WriteLine($"{window.handle.value,-12} {window.processName,-20} {window.title}");
        }
        return Task.FromResult(0);
    }
}
