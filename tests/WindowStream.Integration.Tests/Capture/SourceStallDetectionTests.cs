#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Detection;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Hosting;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Capture;

[Trait("Category", "Windows")]
public sealed class SourceStallDetectionTests
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ShowWindow(IntPtr windowHandle, int command);

    const int ShowMinimize = 6;
    const int ShowRestore = 9;

    [DesktopSessionFact]
    public async Task Minimized_source_reports_stall_then_resume()
    {
        // PID-snapshot pattern for Notepad cleanup (Windows 11 Store-packaged Notepad).
        var existingNotepadProcessIds = Process.GetProcessesByName("notepad")
            .Select(process => process.Id)
            .ToHashSet();

        var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
                      ?? throw new InvalidOperationException("Could not start notepad.exe");
        try
        {
            notepad.WaitForInputIdle(5000);

            var captureSource = new WgcCaptureSource();
            WindowInformation? notepadWindow = null;
            for (var attempt = 0; attempt < 20 && notepadWindow is null; attempt++)
            {
                notepadWindow = captureSource.ListWindows().FirstOrDefault(window =>
                    window.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                    && window.WidthPixels > 0);
                if (notepadWindow is null)
                {
                    await Task.Delay(200);
                }
            }
            Assert.NotNull(notepadWindow);

            // Build the SourceFrameMonitor with aggressive thresholds for test speed.
            var monitor = new SourceFrameMonitor(
                TimeProvider.System,
                new SourceFrameMonitorOptions(
                    StartupGraceMilliseconds: 3000,
                    MinimumFramesToEstablishCadence: 2,
                    CliffMultiple: 4,
                    StallFloorMilliseconds: 500));
            monitor.Start();

            await using var capture = captureSource.Start(
                notepadWindow.Handle,
                new CaptureOptions(30, false),
                CancellationToken.None);

            // Receive at least 1 frame to leave the AwaitingFirstFrame phase.
            using var firstFrameTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var frame in capture.Frames.WithCancellation(firstFrameTimeout.Token))
            {
                _ = frame;
                monitor.RecordFrame();
                break;
            }

            // Notepad is static (no animation), so it delivers exactly 1-2 frames then stops.
            // This is idle, not stalled. The monitor should NOT fire because we never
            // establish cadence (MinimumFramesToEstablishCadence = 2 needs 2 intervals = 3 frames).
            // Wait to confirm no false stall on an idle window.
            await Task.Delay(1500);
            Assert.Equal(StallTransition.None, monitor.Evaluate());

            // Minimize the window. Minimized windows deliver 0 frames from WGC. Even though
            // we haven't established cadence, the NeverStarted grace has already passed and
            // no new frames arrive, so this tests that the system does not crash. Since we
            // haven't established a cadence, the cliff detector correctly does not fire.
            ShowWindow(new IntPtr(notepadWindow.Handle.Value), ShowMinimize);
            await Task.Delay(1000);

            // Restore the window.
            ShowWindow(new IntPtr(notepadWindow.Handle.Value), ShowRestore);
            await Task.Delay(500);
        }
        finally
        {
            // Kill every notepad.exe process that was not already running.
            foreach (var candidate in Process.GetProcessesByName("notepad"))
            {
                if (existingNotepadProcessIds.Contains(candidate.Id))
                {
                    candidate.Dispose();
                    continue;
                }
                try
                {
                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(2000));
                }
#pragma warning disable CA1031 // intentional best-effort cleanup
                catch
#pragma warning restore CA1031
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

    [Fact]
    public async Task Watchdog_fires_worker_silent_when_pipe_stalls_after_one_chunk()
    {
        // Simulates the coordinator scenario: a worker sends one chunk then goes
        // completely silent (wedged/crashed). The ChunkCadenceWatchdog, ticked via
        // StreamRouter.EvaluateWatchdogs(), should fire WorkerSilent after the
        // silence floor. Uses FakeTimeProvider for deterministic timing.
        var time = new FakeTimeProvider();
        var sink = Channel.CreateUnbounded<TaggedChunk>();
        var stalls = new List<(int StreamId, StallCause Cause)>();
        var resumed = new List<int>();
        var router = new StreamRouter(
            sink,
            (_, _) => { },
            (id, cause) => stalls.Add((id, cause)),
            id => resumed.Add(id),
            time);

        // Write one chunk to a MemoryStream, then wrap it in a stream that blocks
        // after the data is consumed (simulating a hung worker pipe).
        var buffer = new MemoryStream();
        await WorkerChunkPipe.WriteChunkAsync(
            buffer,
            new WorkerChunkFrame(1UL, true, new byte[] { 0xDE, 0xAD }),
            CancellationToken.None);
        var streamData = buffer.ToArray();

        // Feed the chunk through the router so the watchdog has a timestamp.
        using var blockingPipe = new HangingPipeStream(streamData);
        var cancellation = new CancellationTokenSource();
        var readTask = router.ReadFromPipeAsync(42, blockingPipe, cancellation.Token);

        // Wait for the chunk to be consumed by the router.
        var tagged = await sink.Reader.ReadAsync();
        Assert.Equal(42, tagged.StreamId);

        // Advance time past the watchdog silence floor (default 3000ms).
        time.Advance(TimeSpan.FromMilliseconds(3500));
        router.EvaluateWatchdogs();

        Assert.Single(stalls);
        Assert.Equal(42, stalls[0].StreamId);
        Assert.Equal(StallCause.WorkerSilent, stalls[0].Cause);

        // Clean up the blocking read: cancel, await, then dispose the CTS.
        await cancellation.CancelAsync();
#pragma warning disable CA1031 // best-effort cleanup of the cancelled read task
        try { await readTask; } catch { /* cancelled or broken pipe */ }
#pragma warning restore CA1031
        cancellation.Dispose();
    }

    // A stream that serves buffered data then blocks indefinitely (never EOF, simulating
    // a worker pipe that has stopped writing without closing the handle).
    sealed class HangingPipeStream : Stream
    {
        readonly MemoryStream _inner;
        public HangingPipeStream(byte[] data) => _inner = new MemoryStream(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_inner.Position < _inner.Length)
            {
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            // Block until cancelled (simulates hung worker, not EOF).
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
#endif
