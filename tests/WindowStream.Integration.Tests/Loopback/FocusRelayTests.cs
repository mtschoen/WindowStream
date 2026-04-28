#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowStream.Core.Capture.Windows;
using WindowStream.Core.Encode;
using WindowStream.Core.Protocol;
using WindowStream.Core.Session.Input;
using WindowStream.Integration.Tests.Infrastructure;
using Xunit;

namespace WindowStream.Integration.Tests.Loopback;

/// <summary>
/// Integration test verifying that a FOCUS_WINDOW message sent over the v2
/// protocol causes Win32 <c>GetForegroundWindow()</c> to return the targeted
/// notepad HWND. This is the most fragile Phase 4 test: it spawns real Notepad
/// processes and exercises the full focus-relay path including the
/// <c>AttachThreadInput</c> dance.
///
/// RISK: Win32 focus-stealing prevention can defeat <c>SetForegroundWindow</c>
/// even with <c>AttachThreadInput</c> when the calling process does not hold the
/// foreground lock (e.g. running from a non-interactive session or under heavy
/// background activity). The test therefore polls <c>GetForegroundWindow</c> for
/// up to 500 ms after sending FOCUS_WINDOW to give the OS time to process the
/// request, and documents the failure mode if it still fails.
/// </summary>
public class FocusRelayTests
{
    // P/Invoke — direct calls for the test-side assertion and setup.
    // The production ForegroundWindowApi is used inside the harness; here we
    // duplicate the minimal calls needed so the test class has no external
    // helper dependency.
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextNative(IntPtr hwnd, string text);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisibleNative(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdNative(IntPtr hwnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindowsNative(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static long GetForegroundWindow() => GetForegroundWindowNative().ToInt64();

    // Encoder options used when registering fake stream entries so OPEN_STREAM
    // succeeds. Dimensions are plausible but we never actually stream video in
    // this test — we only need the coordinator to map streamId → windowId so
    // FOCUS_WINDOW can resolve the HWND.
    private static readonly EncoderOptions DefaultEncoderOptions = new EncoderOptions(
        widthPixels: 1280,
        heightPixels: 720,
        framesPerSecond: 30,
        bitrateBitsPerSecond: 4_000_000,
        groupOfPicturesLength: 30,
        safetyKeyframeIntervalSeconds: 2);

    // Skipped by default: passes only when the test process holds the Win32
    // foreground lock. `dotnet test` rebuilds (or any non-foreground runner)
    // get silently rejected by SetForegroundWindow's focus-stealing prevention.
    // Remove the Skip property locally to verify focus relay end-to-end.
    [DesktopAndNvidiaDriverFact(Skip = "Manual: run from a foreground IDE / terminal — Win32 focus-stealing prevention defeats it otherwise")]
    public async Task FocusWindow_Message_BringsTargetNotepadToForeground()
    {
        // Snapshot existing notepad PIDs before we spawn any so the finally block
        // can kill exactly the processes we created (Windows 11 Store-packaged
        // Notepad launches via a stub that exits immediately; the actual UI
        // process has a different PID).
        HashSet<int> existingNotepadProcessIds = Process.GetProcessesByName("notepad")
            .Select(process => process.Id)
            .ToHashSet();

        // Also snapshot existing notepad-visible HWNDs via WGC so we can
        // identify the two new windows after both Notepad instances launch.
        HashSet<long> existingNotepadHwnds = ListNotepadHwndsViaWgc();

        Process notepadOne = Process.Start(
            new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start first notepad.exe");

        Process notepadTwo = Process.Start(
            new ProcessStartInfo("notepad.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("Could not start second notepad.exe");

        try
        {
            notepadOne.WaitForInputIdle(5000);
            notepadTwo.WaitForInputIdle(5000);

            // Wait for exactly two new notepad windows to appear in the WGC
            // enumeration. This handles both single-process UWP Notepad (where
            // Process.MainWindowHandle returns the same window for both) and
            // legacy multi-process Notepad. Allow up to 6 seconds.
            List<long> newNotepadHwnds = await WaitForNewNotepadHwndsAsync(
                existingNotepadHwnds,
                requiredCount: 2,
                timeoutMilliseconds: 6000);

            Assert.True(
                newNotepadHwnds.Count >= 2,
                $"Expected 2 new notepad windows to appear within 6 s, found {newNotepadHwnds.Count}.");

            IntPtr hwndOne = new IntPtr(newNotepadHwnds[0]);
            IntPtr hwndTwo = new IntPtr(newNotepadHwnds[1]);

            // Give both windows a distinct title so they can be told apart in
            // assertion messages and in the WGC enumerator's title field.
            SetWindowTextNative(hwndOne, "WindowStream-FocusRelayTest-NotepadOne");
            SetWindowTextNative(hwndTwo, "WindowStream-FocusRelayTest-NotepadTwo");

            using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken cancellationToken = cancellation.Token;

            FakeWorkerProcessLauncher fakeWorkerLauncher = new FakeWorkerProcessLauncher();

            // Use the real ForegroundWindowApi so the harness actually invokes
            // AttachThreadInput + SetForegroundWindow rather than the no-op stub.
            // useRealWgcEnumeration drives the 500 ms enumerator timer so
            // WINDOW_ADDED events are pushed to the viewer automatically.
            ForegroundWindowApi realForegroundWindowApi = new ForegroundWindowApi();

            await using CoordinatorLoopbackHarness harness = await CoordinatorLoopbackHarness.StartAsync(
                workerLauncher: fakeWorkerLauncher,
                useRealWgcEnumeration: true,
                foregroundWindowApi: realForegroundWindowApi,
                cancellationToken: cancellationToken);

            // Connect the viewer and complete the HELLO/SERVER_HELLO handshake
            // BEFORE waiting for WINDOW_ADDED events. The enumerator's
            // NotifyWindowAppeared no-ops when no viewer channel is active, so
            // the viewer must be connected first to receive the push events.
            await using FakeViewer viewer = await harness.ConnectViewerAsync(cancellationToken);

            await viewer.SendAsync(
                new HelloMessage(
                    ViewerVersion: 2,
                    DisplayCapabilities: new DisplayCapabilities(1920, 1080, new[] { "h264" })),
                cancellationToken);

            ControlMessage helloResponse = await viewer.ReceiveAsync(cancellationToken);
            ServerHelloMessage serverHello = Assert.IsType<ServerHelloMessage>(helloResponse);
            Assert.True(serverHello.UdpPort > 0);

            // Send VIEWER_READY so the coordinator populates ActiveViewerEndpoint.
            await viewer.SendAsync(
                new ViewerReadyMessage(ViewerUdpPort: viewer.LocalUdpEndpoint.Port),
                cancellationToken);

            // Wait for WINDOW_ADDED messages that carry the two notepad HWNDs.
            // The enumerator ticks every 500 ms; allow up to 10 seconds per window.
            // Non-notepad WINDOW_ADDED events and heartbeats are skipped.
            WindowDescriptor notepadDescriptorOne = await WaitForWindowAddedByHwndAsync(
                viewer, hwndOne.ToInt64(), cancellationToken);
            WindowDescriptor notepadDescriptorTwo = await WaitForWindowAddedByHwndAsync(
                viewer, hwndTwo.ToInt64(), cancellationToken);

            // Register encoder options so OPEN_STREAM can resolve them. The WGC
            // enumerator only populates windowIdToHwnd + windowIdToDescriptor; it
            // does not synthesize EncoderOptions. We inject them here with the
            // real HWND already known from the descriptor.
            harness.InjectWindow(notepadDescriptorOne, notepadDescriptorOne.Hwnd, DefaultEncoderOptions);
            harness.InjectWindow(notepadDescriptorTwo, notepadDescriptorTwo.Hwnd, DefaultEncoderOptions);

            // Open stream 1 against notepad 1. The WGC enumerator continues to
            // push WINDOW_ADDED messages for other desktop windows in the
            // background, so we skip any interleaved non-stream messages when
            // waiting for STREAM_STARTED.
            await viewer.SendAsync(new OpenStreamMessage(notepadDescriptorOne.WindowId), cancellationToken);
            StreamStartedMessage streamStartedOne =
                await WaitForStreamStartedAsync(viewer, cancellationToken);
            int streamIdOne = streamStartedOne.StreamId;

            // Open stream 2 against notepad 2.
            await viewer.SendAsync(new OpenStreamMessage(notepadDescriptorTwo.WindowId), cancellationToken);
            StreamStartedMessage streamStartedTwo =
                await WaitForStreamStartedAsync(viewer, cancellationToken);
            int streamIdTwo = streamStartedTwo.StreamId;

            Assert.NotEqual(streamIdOne, streamIdTwo);

            // Establish a known baseline: bring notepad 1 to the foreground so
            // the "before" state is deterministic before we request focus on notepad 2.
            SetForegroundWindowNative(hwndOne);
            await Task.Delay(100, cancellationToken);

            // Send FOCUS_WINDOW targeting stream 2 (notepad 2).
            await viewer.SendAsync(new FocusWindowMessage(StreamId: streamIdTwo), cancellationToken);

            // Poll GetForegroundWindow for up to 500 ms. Win32 focus changes are
            // asynchronous — the AttachThreadInput dance can succeed while the OS
            // still defers the actual foreground assignment by a few frames.
            long expectedHwnd = hwndTwo.ToInt64();
            bool focusLanded = false;
            Stopwatch pollStopwatch = Stopwatch.StartNew();
            while (pollStopwatch.ElapsedMilliseconds < 500)
            {
                long currentForeground = GetForegroundWindow();
                if (currentForeground == expectedHwnd)
                {
                    focusLanded = true;
                    break;
                }
                await Task.Delay(20, cancellationToken);
            }

            // If focus-stealing prevention defeated the relay (e.g. the test
            // runner process doesn't hold the foreground lock), the assertion
            // will fail with a clear message explaining the Win32 constraint.
            Assert.True(
                focusLanded,
                $"FOCUS_WINDOW(streamId={streamIdTwo}) did not bring notepad 2 " +
                $"(HWND=0x{expectedHwnd:X}) to the foreground within 500 ms. " +
                $"GetForegroundWindow() returned 0x{GetForegroundWindow():X}. " +
                "This can fail when Win32 focus-stealing prevention is active " +
                "(e.g. the test runner does not hold the foreground lock). " +
                "Run the test from an interactive desktop session.");
        }
        finally
        {
            // Kill every notepad.exe process that was not already running before
            // this test launched. Covers both the launcher stub and the UI process
            // on Windows 11 Store-packaged Notepad.
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
            notepadOne.Dispose();
            notepadTwo.Dispose();
        }
    }

    /// <summary>
    /// Uses <see cref="WgcCaptureSource.ListWindows"/> to enumerate all windows
    /// currently visible to WGC whose process name is "notepad". Returns the set
    /// of HWNDs as <c>long</c> values for easy comparison.
    /// </summary>
    private static HashSet<long> ListNotepadHwndsViaWgc()
    {
        WgcCaptureSource captureSource = new WgcCaptureSource();
        return captureSource.ListWindows()
            .Where(window => window.processName.Equals("notepad", StringComparison.OrdinalIgnoreCase))
            .Select(window => window.handle.value)
            .ToHashSet();
    }

    /// <summary>
    /// Polls <see cref="WgcCaptureSource.ListWindows"/> until at least
    /// <paramref name="requiredCount"/> new notepad HWNDs appear (i.e. HWNDs not
    /// present in <paramref name="existingHwnds"/>). Returns the new HWNDs once
    /// the count is reached or the timeout elapses.
    /// </summary>
    private static async Task<List<long>> WaitForNewNotepadHwndsAsync(
        HashSet<long> existingHwnds,
        int requiredCount,
        int timeoutMilliseconds)
    {
        WgcCaptureSource captureSource = new WgcCaptureSource();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            List<long> newHwnds = captureSource.ListWindows()
                .Where(window => window.processName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                                 && !existingHwnds.Contains(window.handle.value)
                                 && window.widthPixels > 0)
                .Select(window => window.handle.value)
                .Distinct()
                .ToList();

            if (newHwnds.Count >= requiredCount)
            {
                return newHwnds;
            }
            await Task.Delay(200);
        }
        // Return whatever was found even if below required count; the assertion
        // in the caller will report the shortfall clearly.
        WgcCaptureSource finalSource = new WgcCaptureSource();
        return finalSource.ListWindows()
            .Where(window => window.processName.Equals("notepad", StringComparison.OrdinalIgnoreCase)
                             && !existingHwnds.Contains(window.handle.value))
            .Select(window => window.handle.value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Reads TCP control messages from the viewer until a
    /// <see cref="StreamStartedMessage"/> arrives. Skips interleaved
    /// <see cref="HeartbeatMessage"/>, <see cref="WindowAddedMessage"/>,
    /// <see cref="WindowUpdatedMessage"/>, and <see cref="WindowRemovedMessage"/>
    /// events that the WGC enumerator pushes in the background.
    /// Throws <see cref="InvalidOperationException"/> if any other message arrives
    /// (e.g. an <see cref="ErrorMessage"/>).
    /// </summary>
    private static async Task<StreamStartedMessage> WaitForStreamStartedAsync(
        FakeViewer viewer,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            ControlMessage message = await viewer.ReceiveAsync(timeoutSource.Token);
            switch (message)
            {
                case StreamStartedMessage started:
                    return started;
                case HeartbeatMessage:
                case WindowAddedMessage:
                case WindowUpdatedMessage:
                case WindowRemovedMessage:
                    // Background enumeration events — skip and keep waiting.
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"Expected STREAM_STARTED but received {message.GetType().Name}: {message}");
            }
        }
    }

    /// <summary>
    /// Reads TCP control messages from the viewer until a <see cref="WindowAddedMessage"/>
    /// arrives whose HWND equals <paramref name="targetHwnd"/>. Skips heartbeats
    /// and WINDOW_ADDED events for other windows.
    /// </summary>
    private static async Task<WindowDescriptor> WaitForWindowAddedByHwndAsync(
        FakeViewer viewer,
        long targetHwnd,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            ControlMessage message = await viewer.ReceiveAsync(timeoutSource.Token);
            if (message is WindowAddedMessage added && added.Window.Hwnd == targetHwnd)
            {
                return added.Window;
            }
            // Skip heartbeats, WINDOW_ADDED for other windows, etc.
        }
    }
}
#endif
