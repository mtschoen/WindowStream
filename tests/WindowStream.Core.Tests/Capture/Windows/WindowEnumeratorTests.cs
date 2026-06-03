using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Windows;

public sealed class WindowEnumeratorTests
{
    sealed class FakeWin32Api : IWin32Api
    {
        public List<FakeWindow> Windows { get; } = new();

        public IEnumerable<IntPtr> EnumerateTopLevelWindowHandles()
        {
            foreach (var window in Windows)
            {
                yield return window.Handle;
            }
        }

        public bool IsWindowVisible(IntPtr handle) =>
            Find(handle)?.Visible ?? false;

        public string GetWindowTitle(IntPtr handle) =>
            Find(handle)?.Title ?? "";

        public string GetWindowClassName(IntPtr handle) =>
            Find(handle)?.ClassName ?? "";

        public (int processIdentifier, string processName) GetWindowProcess(IntPtr handle)
        {
            var w = Find(handle);
            return (w?.ProcessIdentifier ?? 0, w?.ProcessName ?? "");
        }

        public (int widthPixels, int heightPixels) GetWindowSize(IntPtr handle)
        {
            var w = Find(handle);
            return (w?.WidthPixels ?? 0, w?.HeightPixels ?? 0);
        }

        FakeWindow? Find(IntPtr handle) => Windows.Find(w => w.Handle == handle);
    }

    sealed record FakeWindow(
        IntPtr Handle, bool Visible, string Title, string ClassName,
        int ProcessIdentifier, string ProcessName, int WidthPixels, int HeightPixels);

    [Fact]
    public void Enumerate_YieldsOnlyVisibleTitledNonSystemWindows()
    {
        var api = new FakeWin32Api();
        api.Windows.AddRange(new[]
        {
            new FakeWindow(new(1), true,  "Notepad",  "Notepad", 100, "notepad", 640, 480),
            new FakeWindow(new(2), false, "Hidden",   "AnyClass",101, "app",     100, 100),
            new FakeWindow(new(3), true,  "",         "AnyClass",102, "app",     100, 100),
            new FakeWindow(new(4), true,  "Taskbar",  "Shell_TrayWnd", 103, "explorer", 1920, 40),
            new FakeWindow(new(5), true,  "Desktop",  "Progman",       104, "explorer", 1920, 1080),
            new FakeWindow(new(6), true,  "Visible2","ProperClass",   105, "other",    800, 600),
        });

        var enumerator = new WindowEnumerator(api);
        var list = enumerator.EnumerateWindows().ToList();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, window => window.Title == "Notepad");
        Assert.Contains(list, window => window.Title == "Visible2");
    }

    [Fact]
    public void Enumerate_ExcludesZeroSizedWindows()
    {
        var api = new FakeWin32Api();
        api.Windows.Add(new FakeWindow(new(1), true, "Title", "Class", 10, "p", 0, 0));
        var enumerator = new WindowEnumerator(api);
        Assert.Empty(enumerator.EnumerateWindows());
    }

    [Fact]
    public void Enumerate_ReturnsHandleAndDimensions()
    {
        var api = new FakeWin32Api();
        api.Windows.Add(new FakeWindow(new(42), true, "T", "C", 10, "proc", 1024, 768));
        var enumerator = new WindowEnumerator(api);

        var information = enumerator.EnumerateWindows().Single();
        Assert.Equal(42, information.Handle.Value);
        Assert.Equal(1024, information.WidthPixels);
        Assert.Equal(768, information.HeightPixels);
        Assert.Equal("proc", information.ProcessName);
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("WorkerW")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void ExcludedClasses_AreFiltered(string excludedClass)
    {
        var api = new FakeWin32Api();
        api.Windows.Add(new FakeWindow(new(1), true, "T", excludedClass, 10, "p", 100, 100));
        var enumerator = new WindowEnumerator(api);
        Assert.Empty(enumerator.EnumerateWindows());
    }

    [Fact]
    public void Constructor_NullApi_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowEnumerator(null!));
    }
}
