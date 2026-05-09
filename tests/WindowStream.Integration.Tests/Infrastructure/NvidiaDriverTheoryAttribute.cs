using System;
using Xunit;

namespace WindowStream.Integration.Tests.Infrastructure;

/// <summary>
/// Marks a theory that requires an NVIDIA driver with NVENC support.
/// Set WINDOWSTREAM_SKIP_NVENC=1 to skip on machines without an NVIDIA GPU.
/// </summary>
public sealed class NvidiaDriverTheoryAttribute : TheoryAttribute
{
    public NvidiaDriverTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("WINDOWSTREAM_SKIP_NVENC") == "1")
        {
            Skip = "Requires an NVIDIA driver with NVENC (set WINDOWSTREAM_SKIP_NVENC=1 to suppress)";
        }
    }
}
