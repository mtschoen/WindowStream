#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Hosting;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;
using Xunit.Sdk;

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
        var testAssemblyDirectory = Path.GetDirectoryName(
            typeof(WorkerProcessIntegrationTests).Assembly.Location)!;
        var repoRoot = testAssemblyDirectory;
        for (var hops = 0;
             hops < 8 && !File.Exists(Path.Combine(repoRoot, "WindowStream.sln"));
             hops++)
        {
            repoRoot = Path.GetDirectoryName(repoRoot)!;
        }
        Assert.True(
            File.Exists(Path.Combine(repoRoot, "WindowStream.sln")),
            $"could not locate WindowStream.sln walking up from {testAssemblyDirectory}");

        var latencyClockPath = Path.Combine(repoRoot, "tools", "latency-clock.html");
        Assert.True(
            File.Exists(latencyClockPath),
            $"latency-clock.html not found at {latencyClockPath}");
        var latencyClockUri = "file:///" + latencyClockPath.Replace('\\', '/');

        var captureTarget = Process.Start(new ProcessStartInfo("msedge.exe")
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
            var source = new WgcCaptureSource();
            WindowInformation? captureTargetWindow = null;
            for (var attempt = 0; attempt < 40 && captureTargetWindow is null; attempt++)
            {
                captureTargetWindow = source.ListWindows().FirstOrDefault(window =>
                    window.Title.Contains("WindowStream latency clock", StringComparison.OrdinalIgnoreCase)
                    && window.WidthPixels > 0
                    && window.HeightPixels > 0);
                if (captureTargetWindow is null)
                {
                    await Task.Delay(250);
                }
            }
            Assert.NotNull(captureTargetWindow);
            var hwnd = captureTargetWindow.Handle.Value;

            var pipeName = $"windowstream-test-{Guid.NewGuid():N}";
            using var pipeServer = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var encoderOptions = new EncoderOptions(
                widthPixels: 800,
                heightPixels: 600,
                framesPerSecond: 30,
                bitrateBitsPerSecond: 4_000_000,
                groupOfPicturesLength: 30,
                safetyKeyframeIntervalSeconds: 1);
            var encoderOptionsJson = JsonSerializer.Serialize(encoderOptions);

            var cliCsproj = Path.Combine(repoRoot, "src", "WindowStream.Cli", "WindowStream.Cli.csproj");

            var workerStartInfo = new ProcessStartInfo("dotnet")
            {
                Arguments = $"run --project \"{cliCsproj}\" -f net8.0-windows10.0.19041.0 -- "
                            + $"worker --hwnd {hwnd} --stream-id 1 --pipe-name {pipeName} "
                            + $"--encoder-options {EscapeShellArgument(encoderOptionsJson)}",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var worker = Process.Start(workerStartInfo)
                               ?? throw new InvalidOperationException("could not spawn worker");

            // Drain worker stdout/stderr asynchronously so a misbehaving worker
            // doesn't block on a full pipe buffer, and so we can surface its
            // diagnostics if the test fails.
            var workerStandardOutput = new StringBuilder();
            var workerStandardError = new StringBuilder();
            worker.OutputDataReceived += (_, eventArguments) =>
            {
                if (eventArguments.Data is not null)
                {
                    lock (workerStandardOutput) workerStandardOutput.AppendLine(eventArguments.Data);
                }
            };
            worker.ErrorDataReceived += (_, eventArguments) =>
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
                    using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await pipeServer.WaitForConnectionAsync(connectTimeout.Token);

                    var chunkCount = 0;
                    using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (chunkCount < 5)
                    {
                        var frame = await WorkerChunkPipe.ReadChunkAsync(pipeServer, readTimeout.Token);
                        Assert.NotEmpty(frame.Payload);
                        chunkCount++;
                    }
                    // The loop reads exactly 5 chunks or the 30s ReadChunkAsync timeout
                    // fails the test, so reaching here means at least 5 chunks arrived.

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
                    using var drainCancellation = new CancellationTokenSource();
                    // drainCancellation is awaited (drainTask) before its using disposes at scope exit.
                    // ReSharper disable AccessToDisposedClosure
                    var drainTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!drainCancellation.IsCancellationRequested)
                            {
                                await WorkerChunkPipe.ReadChunkAsync(pipeServer, drainCancellation.Token);
                            }
                        }
                        catch (EndOfStreamException) { /* pipe closed — worker shut down */ }
                        catch (OperationCanceledException) { /* drain cancelled */ }
                        #pragma warning disable CA1031 // best-effort drain: pipe may be in any state during teardown
                        catch { /* broken pipe / unexpected — don't mask the real assertion */ }
                        #pragma warning restore CA1031
                    }, drainCancellation.Token);
                    // ReSharper restore AccessToDisposedClosure

                    // Poll HasExited instead of awaiting WaitForExitAsync. The
                    // latter uses the Process.Exited event internally, which
                    // has a known .NET race with redirected stdout/stderr:
                    // when the tracked process is a host (dotnet run) that
                    // spawns a child, WaitForExitAsync can hang indefinitely
                    // waiting for the child's pipe handles to close even after
                    // the host has exited (HasExited=True). Polling avoids this.
                    var exited = false;
                    var exitStopwatch = Stopwatch.StartNew();
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
                        throw new XunitException(
                            "worker did not exit within 15s of Shutdown command. "
                            + $"workerHasExited={worker.HasExited} "
                            + $"stderr:\n{workerStandardError}\nstdout:\n{workerStandardOutput}");
                    }
                    if (worker.ExitCode != 0)
                    {
                        throw new XunitException(
                            $"worker exited with code {worker.ExitCode}. "
                            + $"stderr:\n{workerStandardError}\nstdout:\n{workerStandardOutput}");
                    }
                }
                catch (OperationCanceledException operationCanceledException)
                {
                    // Surface worker diagnostics when a pipe operation times out.
                    throw new XunitException(
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

    static string EscapeShellArgument(string value)
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
