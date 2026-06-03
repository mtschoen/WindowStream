using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public sealed class DisplayCapabilitiesTests
{
    [Fact]
    public void EqualReturnsFalseForNull()
    {
        var subject = new DisplayCapabilities(1920, 1080, new[] { "h264" });
#pragma warning disable CA1508 // CA1508: intentionally exercising Equals(null) returning false
        Assert.False(subject.Equals(null));
#pragma warning restore CA1508
    }

    [Fact]
    public void EqualReturnsTrueForSameReference()
    {
        var subject = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        Assert.True(subject.Equals(subject));
    }

    [Fact]
    public void EqualReturnsTrueForEquivalentValues()
    {
        var first = new DisplayCapabilities(1920, 1080, new[] { "h264", "vp9" });
        var second = new DisplayCapabilities(1920, 1080, new List<string> { "h264", "vp9" });
        Assert.True(first.Equals(second));
    }

    [Fact]
    public void EqualReturnsFalseForDifferentMaximumWidth()
    {
        var first = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        var second = new DisplayCapabilities(1280, 1080, new[] { "h264" });
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void EqualReturnsFalseForDifferentMaximumHeight()
    {
        var first = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        var second = new DisplayCapabilities(1920, 720, new[] { "h264" });
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void EqualReturnsFalseForDifferentCodecs()
    {
        var first = new DisplayCapabilities(1920, 1080, new[] { "h264" });
        var second = new DisplayCapabilities(1920, 1080, new[] { "vp9" });
        Assert.False(first.Equals(second));
    }

    [Fact]
    public void GetHashCodeIsDeterministicForSameValues()
    {
        var first = new DisplayCapabilities(1920, 1080, new[] { "h264", "vp9" });
        var second = new DisplayCapabilities(1920, 1080, new List<string> { "h264", "vp9" });
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void GetHashCodeForEmptyCodecListIsStable()
    {
        var subject = new DisplayCapabilities(0, 0, new List<string>());
        var hashCode = subject.GetHashCode();
        Assert.Equal(hashCode, subject.GetHashCode());
    }
}
