#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Capture;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Loopback;

/// <summary>
/// End-to-end integration test that exercises the full server pipeline on real hardware:
/// WGC capture → NVENC encode → TCP control channel → UDP video delivery → NAL reassembly
/// → software H.264 decode.
///
/// The test spawns Notepad, waits for its window to appear, starts a <see cref="SessionHostLoopbackHarness"/>
/// targeting that window, and asserts that at least two IDR (keyframe) frames arrive with the
/// expected pixel dimensions.
///
/// Skip gates: requires an interactive desktop session AND an NVIDIA driver with NVENC.
/// Both are required because WGC needs a desktop and FFmpegNvencEncoder needs h264_nvenc.
///
/// Notepad cleanup: uses the PID-snapshot pattern from <c>WgcCaptureSourceSmokeTests</c> to
/// kill every notepad.exe process that was not already running before the test started,
/// regardless of how the test ends. This covers Store-packaged Notepad on Windows 11 which
/// launches via a short-lived stub process.
/// </summary>
public sealed class SessionHostLoopbackEndToEndTests
{
    [DesktopAndNvidiaDriverFact]
    [Trait("Category", "Integration")]
    public async Task SessionHost_Produces_Decodable_Idr_Frames_Over_Loopback()
    {
        // Snapshot existing notepad PIDs before launch so we can kill only the new ones.
        HashSet<int> existingNotepadProcessIds = Process.GetProcessesByName("notepad")
            .Select(process => process.Id)
            .ToHashSet();

        Process notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start notepad.exe");

        try
        {
            notepad.WaitForInputIdle(5000);

            // Wait for the Notepad window to appear in the WGC window list.
            WgcCaptureSource captureSource = new WgcCaptureSource();
            WindowInformation? notepadWindow = null;
            for (int attempt = 0; attempt < 30 && notepadWindow is null; attempt++)
            {
                notepadWindow = captureSource.ListWindows().FirstOrDefault(window =>
                    window.processName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                    && window.widthPixels > 0);
                if (notepadWindow is null)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
            Assert.NotNull(notepadWindow);

            // Build the in-process loopback harness and start the full pipeline.
            await using SessionHostLoopbackHarness harness =
                await SessionHostLoopbackHarness.CreateAndStartAsync(
                    notepadWindow!.handle,
                    handshakeTimeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            // Collect frames until we have seen at least two IDR (keyframe) frames, or timeout.
            int idrFrameCount = 0;
            using CancellationTokenSource receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await foreach (DecodedVideoFrame frame in harness.DecodedFrames
                                   .WithCancellation(receiveTimeout.Token)
                                   .ConfigureAwait(false))
                {
                    if (frame.IsKeyframe)
                    {
                        idrFrameCount++;

                        // Pixel dimensions reported by the decoder must match what the server advertised.
                        Assert.Equal(harness.StreamDescriptor.Width, frame.WidthPixels);
                        Assert.Equal(harness.StreamDescriptor.Height, frame.HeightPixels);
                    }

                    if (idrFrameCount >= 2)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout expired — fall through to the assertion below with diagnostic info.
            }

            Assert.True(
                idrFrameCount >= 2,
                $"Expected at least 2 IDR frames but only received {idrFrameCount}. " +
                $"Encoder size: {harness.ComputedEncoderSize.width}x{harness.ComputedEncoderSize.height}, " +
                $"stream descriptor: {harness.StreamDescriptor.Width}x{harness.StreamDescriptor.Height}, " +
                $"UDP packets received: {harness.UdpPacketsReceived}, " +
                $"NAL units reassembled: {harness.NalUnitsReassembled}, " +
                $"frames decoded: {harness.FramesDecoded}.");
        }
        finally
        {
            // Kill every notepad.exe that was not already running before the test started.
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
                    // best-effort
                }
                finally
                {
                    candidate.Dispose();
                }
            }
        }
    }
}
#endif
