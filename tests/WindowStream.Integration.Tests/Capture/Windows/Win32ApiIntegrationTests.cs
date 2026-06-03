#if WINDOWS
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Integration.Tests.Capture.Windows;

[Trait("Category", "Windows")]
public sealed class Win32ApiIntegrationTests
{
    [Fact]
    public void EnumerateTopLevelWindowHandles_ReturnsAtLeastOneWindow()
    {
        var api = new Win32Api();
        Assert.NotEmpty(api.EnumerateTopLevelWindowHandles().Take(1));
    }

    [Fact]
    public void WindowEnumerator_WithRealApi_ReturnsNonZeroVisibleWindows()
    {
        var enumerator = new WindowEnumerator(new Win32Api());
        Assert.NotEmpty(enumerator.EnumerateWindows().Take(1));
    }
}
#endif
