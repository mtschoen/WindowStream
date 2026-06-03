using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace WindowStream.Core.Hosting;

[ExcludeFromCodeCoverage(Justification = "Process spawn + named-pipe handshake; exercised by Phase 4 integration tests.")]
public sealed class WorkerProcessLauncher : IWorkerProcessLauncher
{
    readonly string _executablePath;

    public WorkerProcessLauncher(string executablePath)
    {
        _executablePath = executablePath;
    }

    public async Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeServerStream(
            arguments.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            ArgumentList =
            {
                "worker",
                "--hwnd", arguments.Hwnd.ToString(CultureInfo.InvariantCulture),
                "--stream-id", arguments.StreamId.ToString(CultureInfo.InvariantCulture),
                "--pipe-name", arguments.PipeName,
                "--encoder-options", arguments.EncoderOptionsJson
            },
            UseShellExecute = false,
            RedirectStandardError = true
        };
        var process = Process.Start(processStartInfo)
                      ?? throw new InvalidOperationException("worker spawn failed");
        // Mirror worker stderr to parent stderr so worker-side crashes are visible
        // instead of silently discarded by the redirect.
        var stderrBuffer = new StringBuilder();
        process.ErrorDataReceived += (_, eventArguments) =>
        {
            if (eventArguments.Data is not null)
            {
                stderrBuffer.AppendLine(eventArguments.Data);
                Console.Error.WriteLine($"[worker:{process.Id}] {eventArguments.Data}");
            }
        };
        process.BeginErrorReadLine();
        try
        {
            using var connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // intentional catch-all in pipe handshake; exception is wrapped and rethrown with context
        catch (Exception originalException)
        {
            var exited = process.HasExited;
            int? exitCode = exited ? process.ExitCode : null;
            try
            {
                if (!exited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
#pragma warning disable CA1031 // best-effort kill; process may have already exited
            catch
            {
                // best-effort kill; the process may have already exited
            }
#pragma warning restore CA1031
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"worker pipe handshake failed (exited={exited}, exitCode={exitCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}); " +
                $"worker stderr:{Environment.NewLine}{stderrBuffer}",
                originalException);
        }
#pragma warning restore CA1031
        return new WorkerHandle(process, pipe);
    }

    sealed class WorkerHandle : IWorkerHandle
    {
        readonly Process _process;

        public WorkerHandle(Process process, NamedPipeServerStream pipe)
        {
            _process = process;
            Pipe = pipe;
        }

        public Stream Pipe { get; }

        public int ProcessId => _process.Id;

        public Task<int> WaitForExitAsync()
        {
            var source = new TaskCompletionSource<int>();
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => source.TrySetResult(_process.ExitCode);
            if (_process.HasExited)
            {
                source.TrySetResult(_process.ExitCode);
            }
            return source.Task;
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
#pragma warning disable CA1031 // best-effort kill; process may have already exited
            catch
            {
                // best-effort kill; the process may have already exited
            }
#pragma warning restore CA1031
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try
            {
                await ((NamedPipeServerStream)Pipe).DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // best-effort pipe dispose in async teardown
            catch
            {
                // best-effort pipe dispose during teardown; failure is non-fatal
            }
#pragma warning restore CA1031
            _process.Dispose();
        }
    }
}
