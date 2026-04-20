using System;
using Xunit;

namespace WindowStream.Integration.Tests.Infrastructure;

/// <summary>
/// Marks a test that requires an interactive Windows desktop session.
/// Set WINDOWSTREAM_SKIP_DESKTOP=1 or run headless to skip.
/// </summary>
public sealed class DesktopSessionFactAttribute : FactAttribute
{
    public DesktopSessionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINDOWSTREAM_SKIP_DESKTOP") == "1"
            || !Environment.UserInteractive)
        {
            Skip = "Requires an interactive desktop session (set WINDOWSTREAM_SKIP_DESKTOP=1 to suppress)";
        }
    }
}
