using WindowStream.Core.Protocol;
using Xunit;

namespace WindowStream.Core.Tests.Protocol;

public class WindowDescriptorTests
{
    [Fact]
    public void WindowDescriptor_HoldsAllFields()
    {
        WindowDescriptor descriptor = new WindowDescriptor(
            WindowId: 42,
            Hwnd: 1574208,
            ProcessId: 9876,
            ProcessName: "devenv",
            Title: "Solution1 - Microsoft Visual Studio",
            PhysicalWidth: 1920,
            PhysicalHeight: 1080);

        Assert.Equal(42UL, descriptor.WindowId);
        Assert.Equal(1574208L, descriptor.Hwnd);
        Assert.Equal(9876, descriptor.ProcessId);
        Assert.Equal("devenv", descriptor.ProcessName);
        Assert.Equal("Solution1 - Microsoft Visual Studio", descriptor.Title);
        Assert.Equal(1920, descriptor.PhysicalWidth);
        Assert.Equal(1080, descriptor.PhysicalHeight);
    }
}
