using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace WindowStream.Core.Hosting;

[ExcludeFromCodeCoverage(Justification = "Process spawn + named-pipe handshake; exercised by Phase 4 integration tests.")]
public sealed class WorkerProcessLauncher : IWorkerProcessLauncher
{
    private readonly string executablePath;

    public WorkerProcessLauncher(string executablePath)
    {
        this.executablePath = executablePath;
    }

    public async Task<IWorkerHandle> LaunchAsync(WorkerLaunchArguments arguments, CancellationToken cancellationToken)
    {
        NamedPipeServerStream pipe = new NamedPipeServerStream(
            arguments.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        ProcessStartInfo processStartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            ArgumentList =
            {
                "worker",
                "--hwnd", arguments.Hwnd.ToString(),
                "--stream-id", arguments.StreamId.ToString(),
                "--pipe-name", arguments.PipeName,
                "--encoder-options", arguments.EncoderOptionsJson
            },
            UseShellExecute = false,
            RedirectStandardError = true
        };
        Process process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("worker spawn failed");
        try
        {
            using CancellationTokenSource connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new WorkerHandle(process, pipe);
    }

    private sealed class WorkerHandle : IWorkerHandle
    {
        private readonly Process process;

        public WorkerHandle(Process process, NamedPipeServerStream pipe)
        {
            this.process = process;
            Pipe = pipe;
        }

        public Stream Pipe { get; }

        public Task<int> WaitForExitAsync()
        {
            TaskCompletionSource<int> source = new TaskCompletionSource<int>();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => source.TrySetResult(process.ExitCode);
            if (process.HasExited)
            {
                source.TrySetResult(process.ExitCode);
            }
            return source.Task;
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try
            {
                await ((NamedPipeServerStream)Pipe).DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            process.Dispose();
        }
    }
}
