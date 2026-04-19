using System.Collections.Generic;
using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class DisplayCapabilitiesTests
{
    [Fact]
    public void EqualReturnsFalseForNull()
    {
        DisplayCapabilities subject = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        Assert.False(subject.Equals(null));
    }

    [Fact]
    public void EqualReturnsTrueForSameReference()
    {
        DisplayCapabilities subject = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        Assert.True(subject.Equals(subject));
    }

    [Fact]
    public void EqualReturnsTrueForEquivalentValues()
    {
        DisplayCapabilities first = new DisplayCapabilities(1920, 1080, new[] { "h264", "vp9" });
        DisplayCapabilities second = new DisplayCapabilities(1920, 1080, new List<string> { "h264", "vp9" });
        Assert.True(first.Equals(second));
    }

    [Fact]
    public void EqualReturnsFalseForDifferentMaximumWidth()
    {
        DisplayCapabilities first = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        DisplayCapabilities second = new DisplayCapabilities(1280, 1080, new[] { "h264" });
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void EqualReturnsFalseForDifferentMaximumHeight()
    {
        DisplayCapabilities first = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        DisplayCapabilities second = new DisplayCapabilities(1920, 720, new[] { "h264" });
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void EqualReturnsFalseForDifferentCodecs()
    {
        DisplayCapabilities first = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        DisplayCapabilities second = new DisplayCapabilities(1920, 1080, new[] { "vp9" });
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void GetHashCodeIsDeterministicForSameValues()
    {
        DisplayCapabilities first = new DisplayCapabilities(1920, 1080, new[] { "h264", "vp9" });
        DisplayCapabilities second = new DisplayCapabilities(1920, 1080, new List<string> { "h264", "vp9" });
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void GetHashCodeForEmptyCodecListIsStable()
    {
        DisplayCapabilities subject = new DisplayCapabilities(0, 0, new List<string>());
        int hashCode = subject.GetHashCode();
        Assert.Equal(hashCode, subject.GetHashCode());
    }
}
