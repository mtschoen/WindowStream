#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;

namespace WindowStream.Tools.FrameDeliveryProbe;

// Measures how many WGC frames a window delivers over a fixed duration in a
// chosen window state (none / minimize / offscreen). Validates the premises
// behind the SourceFrameMonitor thresholds: idle => exactly 1-2 frames,
// minimized => 0 frames, animated => continuous, throttle cliff => sharp.
//
// Usage:
//   dotnet run --project tools/frame-delivery-probe -- --title "Notepad" --action none --seconds 6
//   dotnet run --project tools/frame-delivery-probe -- --hwnd 12345 --action minimize --seconds 6 --output results.tsv
static class Program
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    const int ShowMinimize = 6;
    const int ShowRestore = 9;
    const uint SwpNoSize = 0x0001;
    const uint SwpNoZorder = 0x0004;
    const uint SwpNoActivate = 0x0010;

    static async Task<int> Main(string[] arguments)
    {
        var titleFilter = GetArgument(arguments, "--title");
        var hwndArgument = GetArgument(arguments, "--hwnd");
        var action = GetArgument(arguments, "--action") ?? "none";
        var label = GetArgument(arguments, "--label") ?? titleFilter ?? hwndArgument ?? "probe";
        var seconds = int.Parse(
            GetArgument(arguments, "--seconds") ?? "6",
            CultureInfo.InvariantCulture);
        var outputPath = GetArgument(arguments, "--output") ?? "probe-results.tsv";

        if (titleFilter is null && hwndArgument is null)
        {
            await Console.Error.WriteLineAsync("Usage: frame-delivery-probe --title <substring> [--action none|minimize|offscreen] [--seconds 6] [--output results.tsv]");
            await Console.Error.WriteLineAsync("   or: frame-delivery-probe --hwnd <handle> [--action none|minimize|offscreen] [--seconds 6] [--output results.tsv]");
            return 1;
        }

        var source = new WgcCaptureSource();
        WindowInformation? target;

        if (hwndArgument is not null)
        {
            var handle = long.Parse(hwndArgument, CultureInfo.InvariantCulture);
            target = source.ListWindows().FirstOrDefault(window => window.Handle.Value == handle);
        }
        else
        {
            // hwndArgument is null here, and the guard above rejected the case where both
            // titleFilter and hwndArgument are null, so titleFilter is guaranteed non-null.
            var requiredTitleFilter = titleFilter
                ?? throw new InvalidOperationException("titleFilter must be set when hwndArgument is not provided.");
            target = source.ListWindows().FirstOrDefault(window =>
                window.Title.Contains(requiredTitleFilter, StringComparison.OrdinalIgnoreCase)
                && window.WidthPixels > 0
                && window.HeightPixels > 0);
        }

        if (target is null)
        {
            var line = $"{label}\taction={action}\thwnd=NOTFOUND\tframes=-1\tfirstMs=-1\tsecs={seconds}";
            Append(outputPath, line);
            await Console.Error.WriteLineAsync("target window not found");
            return 2;
        }

        var windowHandle = new IntPtr(target.Handle.Value);

        ApplyWindowAction(windowHandle, action);
        await Task.Delay(800);

        var (frameCount, firstFrameMilliseconds, note) = await RunCaptureProbeAsync(source, target, seconds);

        // Restore window state so repeated probes can re-minimize/offscreen.
        RestoreWindowAction(windowHandle, action);

        var result = $"{label}\taction={action}\thwnd={target.Handle.Value}\tframes={frameCount}\tfirstMs={firstFrameMilliseconds}\tsecs={seconds}\tnote={note}";
        Append(outputPath, result);
        Console.WriteLine(result);
        return 0;
    }

    static void ApplyWindowAction(IntPtr windowHandle, string action)
    {
        switch (action)
        {
            case "minimize":
                ShowWindow(windowHandle, ShowMinimize);
                break;
            case "offscreen":
                SetWindowPos(windowHandle, IntPtr.Zero, -5000, -5000, 0, 0,
                    SwpNoSize | SwpNoZorder | SwpNoActivate);
                break;
        }
    }

    static void RestoreWindowAction(IntPtr windowHandle, string action)
    {
        switch (action)
        {
            case "minimize":
                ShowWindow(windowHandle, ShowRestore);
                break;
            case "offscreen":
                SetWindowPos(windowHandle, IntPtr.Zero, 100, 100, 0, 0,
                    SwpNoSize | SwpNoZorder | SwpNoActivate);
                break;
        }
    }

    static async Task<(int frameCount, long firstFrameMilliseconds, string note)> RunCaptureProbeAsync(
        WgcCaptureSource source, WindowInformation target, int seconds)
    {
        var frameCount = 0;
        long firstFrameMilliseconds = -1;
        var note = "ok";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 2));
            await using var capture = source.Start(
                target.Handle,
                new CaptureOptions(TargetFramesPerSecond: 60, IncludeCursor: false),
                cancellation.Token);

            stopwatch.Restart();
            await foreach (var frame in capture.Frames.WithCancellation(cancellation.Token))
            {
                _ = frame;
                if (frameCount == 0)
                {
                    firstFrameMilliseconds = stopwatch.ElapsedMilliseconds;
                }
                frameCount++;
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(seconds))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected when the capture window elapses
        }
        #pragma warning disable CA1031 // probe boundary: log exceptions as data, do not crash
        catch (Exception exception)
        {
            note = "EXC:" + exception.GetType().Name + ":" + exception.Message.Replace('\n', ' ').Replace('\t', ' ');
        }
        #pragma warning restore CA1031

        return (frameCount, firstFrameMilliseconds, note);
    }

    static void Append(string path, string line) =>
        File.AppendAllText(path, line + Environment.NewLine);

    static string? GetArgument(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }
        return null;
    }
}
#endif
