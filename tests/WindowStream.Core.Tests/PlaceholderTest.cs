namespace WindowStream.Core.Tests;

public sealed class PlaceholderTest
{
    [Xunit.Fact]
    public void AssemblyProductNameIsSet()
    {
        Xunit.Assert.Equal("WindowStream.Core", WindowStream.Core.AssemblyInformation.ProductName);
    }
}
