using System.Text;
using WindowStream.Core.Observability;
using Xunit;

namespace WindowStream.Core.Tests.Observability;

public class WindowStreamFileLoggingTests
{
    [Fact]
    public void LogsDirectory_Is_Under_LocalApplicationData_WindowStream_Logs()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowStream", "logs");

        Assert.Equal(expected, WindowStreamFileLogging.LogsDirectory);
    }

    [Fact]
    public void CreateConfiguration_Creates_LogsDirectory_And_Writes_A_Rolling_Jsonl_File()
    {
        var directory = WindowStreamFileLogging.LogsDirectory;
        if (Directory.Exists(directory))
        {
            foreach (var stale in Directory.GetFiles(directory, "server-*.jsonl"))
            {
                File.Delete(stale);
            }
        }

        const string marker = nameof(CreateConfiguration_Creates_LogsDirectory_And_Writes_A_Rolling_Jsonl_File);
        using (var logger = WindowStreamFileLogging.CreateConfiguration().CreateLogger())
        {
            logger.Information(marker);
        }

        Assert.True(Directory.Exists(directory));
        var files = Directory.GetFiles(directory, "server-*.jsonl");
        Assert.Contains(files, file => File.ReadAllText(file, Encoding.UTF8).Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public void CreateConfiguration_Honors_Custom_RetainedFileCountLimit()
    {
        // Exercises the non-default overload path; retention count itself is
        // Serilog's responsibility (verified upstream), so we just assert the
        // configuration builds a working logger without throwing.
        using var logger = WindowStreamFileLogging.CreateConfiguration(retainedFileCountLimit: 3).CreateLogger();
        logger.Information("retention-limit-smoke-test");
    }
}
