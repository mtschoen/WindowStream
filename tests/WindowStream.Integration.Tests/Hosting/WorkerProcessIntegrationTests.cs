#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
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
        // Snapshot existing notepad PIDs and kill any new ones in finally.
        // Mirrors WgcCaptureSourceSmokeTests' pattern; needed because Win11
        // Store-packaged notepad uses a stub launcher process whose Process
        // handle exits immediately and doesn't track the actual window.
        HashSet<int> existingNotepadProcessIds = Process.GetProcessesByName("notepad")
            .Select(process => process.Id)
            .ToHashSet();

        Process notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start notepad.exe");

        try
        {
            notepad.WaitForInputIdle(5000);

            // Find a notepad window via WGC enumeration. Loop briefly because
            // the launcher-stub pattern means the window may take a moment to
            // appear after Process.Start returns. WindowInformation does not
            // expose processId, so we match by processName + non-zero size
            // (mirroring WgcCaptureSourceSmokeTests). The finally block kills
            // every new notepad process regardless of which window we picked.
            WgcCaptureSource source = new WgcCaptureSource();
            WindowInformation? notepadWindow = null;
            for (int attempt = 0; attempt < 20 && notepadWindow is null; attempt++)
            {
                notepadWindow = source.ListWindows().FirstOrDefault(window =>
                    window.processName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                    && window.widthPixels > 0
                    && window.heightPixels > 0);
                if (notepadWindow is null)
                {
                    await Task.Delay(250);
                }
            }
            Assert.NotNull(notepadWindow);
            long hwnd = notepadWindow!.handle.value;

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

            // Resolve the CLI csproj path by walking up from the test assembly
            // until we find WindowStream.sln. Avoids hard-coding a relative
            // path that breaks if bin layout changes.
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

            // Notepad with no input emits exactly one WGC frame and then sits
            // silent (per the project's "static windows" gotcha). To exercise
            // the worker's encode pump we keep the captured window mutating:
            // every tick we move the window between two positions a few pixels
            // apart and force a synchronous redraw. We use MoveWindow (not
            // SetWindowPos) because MoveWindow with bRepaint=TRUE issues a
            // synchronous WM_PAINT, which DWM honors and which WGC observes
            // as a fresh frame.
            IntPtr notepadHwnd = new IntPtr(hwnd);
            Win32Geometry.GetWindowRect(notepadHwnd, out Win32Rect originalRect);
            int originalWidth = originalRect.Right - originalRect.Left;
            int originalHeight = originalRect.Bottom - originalRect.Top;
            using CancellationTokenSource frameNudgerCancellation = new CancellationTokenSource();
            Task frameNudgerTask = Task.Run(async () =>
            {
                try
                {
                    bool toggle = false;
                    while (!frameNudgerCancellation.IsCancellationRequested)
                    {
                        toggle = !toggle;
                        Win32Geometry.MoveWindow(
                            notepadHwnd,
                            originalRect.Left + (toggle ? 4 : 0),
                            originalRect.Top,
                            originalWidth,
                            originalHeight,
                            bRepaint: true);
                        Win32Geometry.RedrawWindow(
                            notepadHwnd,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            Win32Geometry.RDW_INVALIDATE | Win32Geometry.RDW_UPDATENOW | Win32Geometry.RDW_ALLCHILDREN);
                        await Task.Delay(50, frameNudgerCancellation.Token);
                    }
                }
                catch (OperationCanceledException) { /* expected on shutdown */ }
            });

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

                    bool exited = worker.WaitForExit(5000);
                    if (!exited)
                    {
                        throw new Xunit.Sdk.XunitException(
                            "worker did not exit within 5s of Shutdown command. "
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
                        + $"workerExitCode={(worker.HasExited ? worker.ExitCode.ToString() : "n/a")}\n"
                        + $"stderr:\n{workerStandardError}\nstdout:\n{workerStandardOutput}",
                        operationCanceledException);
                }
            }
            finally
            {
                frameNudgerCancellation.Cancel();
                try { await frameNudgerTask; } catch { /* best-effort */ }
                if (!worker.HasExited)
                {
                    try { worker.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
                    worker.WaitForExit(2000);
                }
            }
        }
        finally
        {
            // Kill every notepad.exe process that wasn't already running before
            // we launched (covers both the launcher stub and the Store-launched
            // UI process on Windows 11).
            foreach (Process candidate in Process.GetProcessesByName("notepad"))
            {
                if (existingNotepadProcessIds.Contains(candidate.Id))
                {
                    candidate.Dispose();
                    continue;
                }
                try
                {
                    candidate.Kill(entireProcessTree: true);
                    candidate.WaitForExit(2000);
                }
                catch
                {
                    // best-effort cleanup
                }
                finally
                {
                    candidate.Dispose();
                }
            }
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
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class Win32Geometry
    {
        public const uint RDW_INVALIDATE = 0x0001;
        public const uint RDW_UPDATENOW = 0x0100;
        public const uint RDW_ALLCHILDREN = 0x0080;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(
            IntPtr hWnd,
            int X,
            int Y,
            int nWidth,
            int nHeight,
            [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RedrawWindow(
            IntPtr hWnd,
            IntPtr lprcUpdate,
            IntPtr hrgnUpdate,
            uint flags);
    }
}
#endif
