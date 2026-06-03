using WindowStream.Core.Capture;

namespace WindowStream.Cli.Commands;

public sealed class ListWindowsCommandHandler
{
    readonly IWindowCaptureSource _captureSource;
    readonly TextWriter _writer;

    public ListWindowsCommandHandler(IWindowCaptureSource captureSource, TextWriter writer)
    {
        _captureSource = captureSource;
        _writer = writer;
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        await _writer.WriteLineAsync($"{"HANDLE",-12} {"PROCESS",-20} TITLE").ConfigureAwait(false);
        foreach (var window in _captureSource.ListWindows())
        {
            await _writer.WriteLineAsync($"{window.Handle.Value,-12} {window.ProcessName,-20} {window.Title}").ConfigureAwait(false);
        }
        return 0;
    }
}
