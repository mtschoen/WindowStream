using Xunit;

namespace WindowStream.Core.Tests;

public sealed class PlaceholderTest
{
    [Fact]
    public void AssemblyProductNameIsSet()
    {
        Assert.Equal("WindowStream.Core", AssemblyInformation.ProductName);
    }
}
