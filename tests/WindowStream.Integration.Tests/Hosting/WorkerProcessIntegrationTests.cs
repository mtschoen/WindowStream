#if WINDOWS
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Hosting;

[Trait("Category", "Windows")]
public sealed class WorkerProcessIntegrationTests
{
    [DesktopAndNvidiaDriverFact]
    public async Task WorkerEmitsChunksThroughPipe()
    {
        // Launch the latency-clock HTML in Edge as the capture target.
        // The latency clock redraws at requestAnimationFrame speed (~60–165 fps),
        // so WGC delivers a continuous frame stream without any MoveWindow/
        // RedrawWindow nudger. Edge's --app flag opens a clean window with a
        // unique title ("WindowStream latency clock") for reliable WGC lookup.
        // Edge is always present on Windows 10+; msedge.exe is a plain Win32
        // process — no Win11 Store-packaged launcher-stub indirection.
        string testAssemblyDirectory = System.IO.Path.GetDirectoryName(
            typeof(WorkerProcessIntegrationTests).Assembly.Location)!;
        string repoRoot = testAssemblyDirectory;
        for (int hops = 0;
             hops < 8 && !System.IO.File.Exists(System.IO.Path.Combine(repoRoot, "WindowStream.sln"));
             hops++)
        {
            repoRoot = System.IO.Path.GetDirectoryName(repoRoot)!;
        }
        Assert.True(
            System.IO.File.Exists(System.IO.Path.Combine(repoRoot, "WindowStream.sln")),
            $"could not locate WindowStream.sln walking up from {testAssemblyDirectory}");

        string latencyClockPath = System.IO.Path.Combine(repoRoot, "tools", "latency-clock.html");
        Assert.True(
            System.IO.File.Exists(latencyClockPath),
            $"latency-clock.html not found at {latencyClockPath}");
        string latencyClockUri = "file:///" + latencyClockPath.Replace('\\', '/');

        Process captureTarget = Process.Start(new ProcessStartInfo("msedge.exe")
        {
            Arguments = $"--app=\"{latencyClockUri}\" --new-window --no-first-run --disable-extensions",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Could not start msedge.exe");

        try
        {
            await Task.Delay(2000);

            // Find the latency-clock window via WGC enumeration. Match by
            // window title rather than process name to avoid collisions with
            // other Edge windows. The latency-clock HTML sets its <title> to
            // "WindowStream latency clock".
            WgcCaptureSource source = new WgcCaptureSource();
            WindowInformation? captureTargetWindow = null;
            for (int attempt = 0; attempt < 40 && captureTargetWindow is null; attempt++)
            {
                captureTargetWindow = source.ListWindows().FirstOrDefault(window =>
                    window.title.Contains("WindowStream latency clock", StringComparison.OrdinalIgnoreCase)
                    && window.widthPixels > 0
                    && window.heightPixels > 0);
                if (captureTargetWindow is null)
                {
                    await Task.Delay(250);
                }
            }
            Assert.NotNull(captureTargetWindow);
            long hwnd = captureTargetWindow!.handle.value;

            string pipeName = $"windowstream-test-{Guid.NewGuid():N}";
            using NamedPipeServerStream pipeServer = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            EncoderOptions encoderOptions = new EncoderOptions(
                widthPixels: 800,
                heightPixels: 600,
                framesPerSecond: 30,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 1);
            string encoderOptionsJson = JsonSerializer.Serialize(encoderOptions);

            string cliCsproj = System.IO.Path.Combine(repoRoot, "src", "WindowStream.Cli", "WindowStream.Cli.csproj");

            ProcessStartInfo workerStartInfo = new ProcessStartInfo("dotnet")
            {
                Arguments = $"run --project \"{cliCsproj}\" -f net8.0-windows10.0.19041.0 -- "
                            + $"worker --hwnd {hwnd} --stream-id 1 --pipe-name {pipeName} "
                            + $"--encoder-options {EscapeShellArgument(encoderOptionsJson)}",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using Process worker = Process.Start(workerStartInfo)
                ?? throw new InvalidOperationException("could not spawn worker");

            // Drain worker stdout/stderr asynchronously so a misbehaving worker
            // doesn't block on a full pipe buffer, and so we can surface its
            // diagnostics if the test fails.
            System.Text.StringBuilder workerStandardOutput = new System.Text.StringBuilder();
            System.Text.StringBuilder workerStandardError = new System.Text.StringBuilder();
            worker.OutputDataReceived += (sender, eventArguments) =>
            {
                if (eventArguments.Data is not null)
                {
                    lock (workerStandardOutput) workerStandardOutput.AppendLine(eventArguments.Data);
                }
            };
            worker.ErrorDataReceived += (sender, eventArguments) =>
            {
                if (eventArguments.Data is not null)
                {
                    lock (workerStandardError) workerStandardError.AppendLine(eventArguments.Data);
                }
            };
            worker.BeginOutputReadLine();
            worker.BeginErrorReadLine();

            try
            {
                try
                {
                    using CancellationTokenSource connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await pipeServer.WaitForConnectionAsync(connectTimeout.Token);

                    int chunkCount = 0;
                    using CancellationTokenSource readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (chunkCount < 5)
                    {
                        WorkerChunkFrame frame = await WorkerChunkPipe.ReadChunkAsync(pipeServer, readTimeout.Token);
                        Assert.NotEmpty(frame.Payload);
                        chunkCount++;
                    }
                    Assert.True(chunkCount >= 5, $"expected at least 5 chunks, got {chunkCount}");

                    await WorkerChunkPipe.WriteCommandAsync(
                        pipeServer,
                        new WorkerCommandFrame(WorkerCommandTag.Shutdown),
                        CancellationToken.None);

                    // Keep draining chunks from the pipe after sending Shutdown.
                    // The worker's encodeOutputTask may still be writing chunks
                    // it had queued before the lifecycle token cancelled. If the
                    // test stops reading, pipe backpressure blocks the worker's
                    // WriteChunkAsync and prevents graceful exit. Drain until
                    // the pipe breaks (EndOfStreamException) or the worker exits.
                    using CancellationTokenSource drainCancellation = new CancellationTokenSource();
                    Task drainTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!drainCancellation.IsCancellationRequested)
                            {
                                await WorkerChunkPipe.ReadChunkAsync(pipeServer, drainCancellation.Token);
                            }
                        }
                        catch (System.IO.EndOfStreamException) { /* pipe closed — worker shut down */ }
                        catch (OperationCanceledException) { /* drain cancelled */ }
                        #pragma warning disable CA1031 // best-effort drain: pipe may be in any state during teardown
                        catch { /* broken pipe / unexpected — don't mask the real assertion */ }
                        #pragma warning restore CA1031
                    }, drainCancellation.Token);

                    // Poll HasExited instead of awaiting WaitForExitAsync. The
                    // latter uses the Process.Exited event internally, which
                    // has a known .NET race with redirected stdout/stderr:
                    // when the tracked process is a host (dotnet run) that
                    // spawns a child, WaitForExitAsync can hang indefinitely
                    // waiting for the child's pipe handles to close even after
                    // the host has exited (HasExited=True). Polling avoids this.
                    bool exited = false;
                    Stopwatch exitStopwatch = Stopwatch.StartNew();
                    while (exitStopwatch.Elapsed < TimeSpan.FromSeconds(15))
                    {
                        if (worker.HasExited)
                        {
                            exited = true;
                            break;
                        }
                        await Task.Delay(200);
                    }
                    await drainCancellation.CancelAsync();
                    #pragma warning disable CA1031 // best-effort: drain task may fault on cancelled token
                    try { await drainTask; } catch { /* best-effort */ }
                    #pragma warning restore CA1031
                    if (!exited)
                    {
                        throw new Xunit.Sdk.XunitException(
                            "worker did not exit within 15s of Shutdown command. "
                            + $"workerHasExited={worker.HasExited} "
                            + $"stderr:\n{workerStandardError}\nstdout:\n{workerStandardOutput}");
                    }
                    if (worker.ExitCode != 0)
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"worker exited with code {worker.ExitCode}. "
                            + $"stderr:\n{workerStandardError}\nstdout:\n{workerStandardOutput}");
                    }
                }
                catch (OperationCanceledException operationCanceledException)
                {
                    // Surface worker diagnostics when a pipe operation times out.
                    throw new Xunit.Sdk.XunitException(
                        "worker pipe operation timed out. "
                        + $"workerHasExited={worker.HasExited} "
                        + $"workerExitCode={(worker.HasExited ? worker.ExitCode.ToString(CultureInfo.InvariantCulture) : "n/a")}\n"
                        + $"stderr:\n{workerStandardError}\nstdout:\n{workerStandardOutput}",
                        operationCanceledException);
                }
            }
            finally
            {
                if (!worker.HasExited)
                {
                    #pragma warning disable CA1031 // best-effort cleanup — Kill can throw on already-exited process
                    try { worker.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
                    #pragma warning restore CA1031
                }
            }
        }
        finally
        {
            // Kill the Edge app-mode process. entireProcessTree:true ensures the
            // renderer child processes are cleaned up too.
            if (!captureTarget.HasExited)
            {
                #pragma warning disable CA1031 // best-effort cleanup — Kill can throw on already-exited process
                try { captureTarget.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
                #pragma warning restore CA1031
            }
            captureTarget.Dispose();
        }
    }

    private static string EscapeShellArgument(string value)
    {
        // Windows command-line quoting: wrap in double quotes and escape
        // embedded double quotes by preceding them with a backslash.
        // Acceptable for JSON payloads which contain double quotes but no
        // backslashes that need special handling. Backslashes immediately
        // preceding the closing quote would also need doubling, but JSON
        // EncoderOptions never end that way.
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
#endif
