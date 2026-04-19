#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture;

[Trait("Category", "Windows")]
public sealed class WgcCaptureSourceSmokeTests
{
    [Fact(Timeout = 15000)]
    public async Task Attaches_To_Notepad_And_Receives_Frame()
    {
        // On Windows 11 the Store-packaged Notepad launches via a stub process
        // that exits immediately; CloseMainWindow on the returned Process does
        // nothing because the actual UI process is different. Snapshot existing
        // notepad PIDs before launch and kill any new ones in the finally block.
        HashSet<int> existingNotepadProcessIds = Process.GetProcessesByName("notepad")
            .Select(process => process.Id)
            .ToHashSet();

        Process notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start notepad.exe");
        try
        {
            notepad.WaitForInputIdle(5000);

            WgcCaptureSource source = new WgcCaptureSource();
            WindowInformation? notepadWindow = null;
            for (int attempt = 0; attempt < 20 && notepadWindow is null; attempt++)
            {
                notepadWindow = source.ListWindows().FirstOrDefault(window =>
                    window.processName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                    && window.widthPixels > 0);
                if (notepadWindow is null)
                {
                    await Task.Delay(200);
                }
            }
            Assert.NotNull(notepadWindow);

            await using IWindowCapture capture = source.Start(
                notepadWindow!.handle,
                new CaptureOptions(30, false),
                CancellationToken.None);

            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (CapturedFrame frame in capture.Frames.WithCancellation(timeout.Token))
            {
                Assert.True(frame.widthPixels > 0);
                Assert.True(frame.heightPixels > 0);
                return;
            }
            Assert.Fail("No frame received before timeout.");
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
}
#endif
