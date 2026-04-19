using System.Collections.Generic;
using System.Linq;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Windows;

public sealed class WindowEnumeratorTests
{
    private sealed class FakeWin32Api : IWin32Api
    {
        public List<FakeWindow> windows { get; } = new();

        public IEnumerable<System.IntPtr> EnumerateTopLevelWindowHandles()
        {
            foreach (FakeWindow window in windows)
            {
                yield return window.handle;
            }
        }

        public bool IsWindowVisible(System.IntPtr handle) =>
            Find(handle)?.visible ?? false;

        public string GetWindowTitle(System.IntPtr handle) =>
            Find(handle)?.title ?? "";

        public string GetWindowClassName(System.IntPtr handle) =>
            Find(handle)?.className ?? "";

        public (int processIdentifier, string processName) GetWindowProcess(System.IntPtr handle)
        {
            FakeWindow? w = Find(handle);
            return (w?.processIdentifier ?? 0, w?.processName ?? "");
        }

        public (int widthPixels, int heightPixels) GetWindowSize(System.IntPtr handle)
        {
            FakeWindow? w = Find(handle);
            return (w?.widthPixels ?? 0, w?.heightPixels ?? 0);
        }

        private FakeWindow? Find(System.IntPtr handle) => windows.Find(w => w.handle == handle);
    }

    private sealed record FakeWindow(
        System.IntPtr handle, bool visible, string title, string className,
        int processIdentifier, string processName, int widthPixels, int heightPixels);

    [Fact]
    public void Enumerate_YieldsOnlyVisibleTitledNonSystemWindows()
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.AddRange(new[]
        {
            new FakeWindow(new(1), true,  "Notepad",  "Notepad", 100, "notepad", 640, 480),
            new FakeWindow(new(2), false, "Hidden",   "AnyClass",101, "app",     100, 100),
            new FakeWindow(new(3), true,  "",         "AnyClass",102, "app",     100, 100),
            new FakeWindow(new(4), true,  "Taskbar",  "Shell_TrayWnd", 103, "explorer", 1920, 40),
            new FakeWindow(new(5), true,  "Desktop",  "Progman",       104, "explorer", 1920, 1080),
            new FakeWindow(new(6), true,  "Visible2","ProperClass",   105, "other",    800, 600),
        });

        WindowEnumerator enumerator = new WindowEnumerator(api);
        List<WindowInformation> list = enumerator.EnumerateWindows().ToList();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, window => window.title == "Notepad");
        Assert.Contains(list, window => window.title == "Visible2");
    }

    [Fact]
    public void Enumerate_ExcludesZeroSizedWindows()
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.Add(new FakeWindow(new(1), true, "Title", "Class", 10, "p", 0, 0));
        WindowEnumerator enumerator = new WindowEnumerator(api);
        Assert.Empty(enumerator.EnumerateWindows());
    }

    [Fact]
    public void Enumerate_ReturnsHandleAndDimensions()
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.Add(new FakeWindow(new(42), true, "T", "C", 10, "proc", 1024, 768));
        WindowEnumerator enumerator = new WindowEnumerator(api);

        WindowInformation information = enumerator.EnumerateWindows().Single();
        Assert.Equal(42, information.handle.value);
        Assert.Equal(1024, information.widthPixels);
        Assert.Equal(768, information.heightPixels);
        Assert.Equal("proc", information.processName);
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("WorkerW")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void ExcludedClasses_AreFiltered(string excludedClass)
    {
        FakeWin32Api api = new FakeWin32Api();
        api.windows.Add(new FakeWindow(new(1), true, "T", excludedClass, 10, "p", 100, 100));
        WindowEnumerator enumerator = new WindowEnumerator(api);
        Assert.Empty(enumerator.EnumerateWindows());
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() => new WindowEnumerator(null!));
    }
}
