using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Text;
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
        // Mirror worker stderr to parent stderr so worker-side crashes are visible
        // instead of silently discarded by the redirect.
        StringBuilder stderrBuffer = new StringBuilder();
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
            using CancellationTokenSource connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception originalException)
        {
            bool exited = process.HasExited;
            int? exitCode = exited ? process.ExitCode : null;
            try
            {
                if (!exited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"worker pipe handshake failed (exited={exited}, exitCode={exitCode?.ToString() ?? "n/a"}); " +
                $"worker stderr:{System.Environment.NewLine}{stderrBuffer}",
                originalException);
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
