using Serilog;
using Serilog.Formatting.Compact;

namespace WindowStream.Core.Observability;

/// <summary>
/// Shared Serilog file-sink wiring for the rolling JSONL diagnostics log
/// (<c>%LOCALAPPDATA%\WindowStream\logs\server-YYYY-MM-DD.jsonl</c>). Both
/// launch paths (the MAUI flavor's <c>MauiProgram</c> and the CLI's
/// <c>CliServices</c>) call <see cref="CreateConfiguration"/> so the log
/// location and retention stay identical regardless of which flavor started
/// the server.
/// </summary>
public static class WindowStreamFileLogging
{
    public const int DefaultRetainedFileCountLimit = 7;

    public static string LogsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowStream", "logs");

    /// <summary>
    /// Builds a <see cref="LoggerConfiguration"/> with the rolling JSONL file
    /// sink wired in. Callers may chain additional sinks (e.g. an in-app
    /// dashboard sink) before calling <c>CreateLogger()</c>.
    /// </summary>
    public static LoggerConfiguration CreateConfiguration(int retainedFileCountLimit = DefaultRetainedFileCountLimit)
    {
        var directory = LogsDirectory;
        Directory.CreateDirectory(directory);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: Path.Combine(directory, "server-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedFileCountLimit,
                shared: false);
    }
}
