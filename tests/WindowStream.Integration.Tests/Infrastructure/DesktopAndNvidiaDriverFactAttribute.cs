using System;
using Xunit;

namespace WindowStream.Integration.Tests.Infrastructure;

/// <summary>
/// Combined skip gate for tests that require both an interactive desktop session
/// and an NVIDIA driver with NVENC support.
/// </summary>
public sealed class DesktopAndNvidiaDriverFactAttribute : FactAttribute
{
    public DesktopAndNvidiaDriverFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINDOWSTREAM_SKIP_NVENC") == "1")
        {
            Skip = "Requires NVIDIA driver with NVENC (set WINDOWSTREAM_SKIP_NVENC=1 to suppress)";
            return;
        }

        if (Environment.GetEnvironmentVariable("WINDOWSTREAM_SKIP_DESKTOP") == "1"
            || !Environment.UserInteractive)
        {
            Skip = "Requires an interactive desktop session (set WINDOWSTREAM_SKIP_DESKTOP=1 to suppress)";
        }
    }
}
