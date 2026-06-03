using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Windows;

public sealed class WindowIdentityRegistryTests
{
    static WindowInformation Win(long handle, string title, int widthPixels = 800, int heightPixels = 600)
        => new WindowInformation(
            Handle: new WindowHandle(handle),
            Title: title,
            ProcessName: "test",
            WidthPixels: widthPixels,
            HeightPixels: heightPixels);

    [Fact]
    public void NewWindow_GetsAppearedEvent_WithFreshId()
    {
        var registry = new WindowIdentityRegistry();
        var events = registry.Diff(new[] { Win(0x100, "a") }).ToArray();
        Assert.Single(events);
        var appeared = Assert.IsType<WindowAppeared>(events[0]);
        Assert.Equal(1UL, appeared.WindowId);
    }

    [Fact]
    public void IdsAreMonotonic()
    {
        var registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        var events = registry.Diff(new[] { Win(0x100, "a"), Win(0x200, "b") }).ToArray();
        var appeared = Assert.IsType<WindowAppeared>(events.Single());
        Assert.Equal(2UL, appeared.WindowId);
    }

    [Fact]
    public void TitleChange_EmitsWindowChanged_KeepsId()
    {
        var registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "old") });
        var events = registry.Diff(new[] { Win(0x100, "new") }).ToArray();
        var changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1UL, changed.WindowId);
        Assert.Equal("new", changed.NewTitle);
    }

    [Fact]
    public void DimensionChange_EmitsWindowChanged()
    {
        var registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        var events = registry.Diff(new[] { Win(0x100, "a", 1024, 768) }).ToArray();
        var changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1024, changed.NewWidthPixels);
        Assert.Equal(768, changed.NewHeightPixels);
        Assert.Null(changed.NewTitle);
    }

    [Fact]
    public void TitleAndDimensionChange_EmitsSingleWindowChanged_WithAllDeltas()
    {
        var registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "old") });
        var events = registry.Diff(new[] { Win(0x100, "new", 1024, 768) }).ToArray();
        var changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1UL, changed.WindowId);
        Assert.Equal("new", changed.NewTitle);
        Assert.Equal(1024, changed.NewWidthPixels);
        Assert.Equal(768, changed.NewHeightPixels);
    }

    [Fact]
    public void HandleGone_EmitsDisappeared()
    {
        var registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        var events = registry.Diff(Array.Empty<WindowInformation>()).ToArray();
        var gone = Assert.IsType<WindowDisappeared>(events.Single());
        Assert.Equal(1UL, gone.WindowId);
    }

    [Fact]
    public void ReusedHandle_GetsFreshId()
    {
        var registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        registry.Diff(Array.Empty<WindowInformation>());
        var events = registry.Diff(new[] { Win(0x100, "b") }).ToArray();
        var appeared = Assert.IsType<WindowAppeared>(events.Single());
        Assert.Equal(2UL, appeared.WindowId);
    }

    [Fact]
    public void NoChange_EmitsNoEvents()
    {
        var registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        Assert.Empty(registry.Diff(new[] { Win(0x100, "a") }));
    }
}
