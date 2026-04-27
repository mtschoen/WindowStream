using System;
using System.Linq;
using WindowStream.Core.Capture;
using WindowStream.Core.Capture.Windows;
using Xunit;

namespace WindowStream.Core.Tests.Capture.Windows;

public sealed class WindowIdentityRegistryTests
{
    private static WindowInformation Win(long handle, string title, int widthPixels = 800, int heightPixels = 600)
        => new WindowInformation(
            handle: new WindowHandle(handle),
            title: title,
            processName: "test",
            widthPixels: widthPixels,
            heightPixels: heightPixels);

    [Fact]
    public void NewWindow_GetsAppearedEvent_WithFreshId()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        WindowEnumerationEvent[] events = registry.Diff(new[] { Win(0x100, "a") }).ToArray();
        Assert.Single(events);
        WindowAppeared appeared = Assert.IsType<WindowAppeared>(events[0]);
        Assert.Equal(1UL, appeared.WindowId);
    }

    [Fact]
    public void IdsAreMonotonic()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        WindowEnumerationEvent[] events = registry.Diff(new[] { Win(0x100, "a"), Win(0x200, "b") }).ToArray();
        WindowAppeared appeared = Assert.IsType<WindowAppeared>(events.Single());
        Assert.Equal(2UL, appeared.WindowId);
    }

    [Fact]
    public void TitleChange_EmitsWindowChanged_KeepsId()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "old") });
        WindowEnumerationEvent[] events = registry.Diff(new[] { Win(0x100, "new") }).ToArray();
        WindowChanged changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1UL, changed.WindowId);
        Assert.Equal("new", changed.NewTitle);
    }

    [Fact]
    public void DimensionChange_EmitsWindowChanged()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a", 800, 600) });
        WindowEnumerationEvent[] events = registry.Diff(new[] { Win(0x100, "a", 1024, 768) }).ToArray();
        WindowChanged changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1024, changed.NewWidthPixels);
        Assert.Equal(768, changed.NewHeightPixels);
        Assert.Null(changed.NewTitle);
    }

    [Fact]
    public void TitleAndDimensionChange_EmitsSingleWindowChanged_WithAllDeltas()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "old", 800, 600) });
        WindowEnumerationEvent[] events = registry.Diff(new[] { Win(0x100, "new", 1024, 768) }).ToArray();
        WindowChanged changed = Assert.IsType<WindowChanged>(events.Single());
        Assert.Equal(1UL, changed.WindowId);
        Assert.Equal("new", changed.NewTitle);
        Assert.Equal(1024, changed.NewWidthPixels);
        Assert.Equal(768, changed.NewHeightPixels);
    }

    [Fact]
    public void HandleGone_EmitsDisappeared()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        WindowEnumerationEvent[] events = registry.Diff(Array.Empty<WindowInformation>()).ToArray();
        WindowDisappeared gone = Assert.IsType<WindowDisappeared>(events.Single());
        Assert.Equal(1UL, gone.WindowId);
    }

    [Fact]
    public void ReusedHandle_GetsFreshId()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        registry.Diff(Array.Empty<WindowInformation>());
        WindowEnumerationEvent[] events = registry.Diff(new[] { Win(0x100, "b") }).ToArray();
        WindowAppeared appeared = Assert.IsType<WindowAppeared>(events.Single());
        Assert.Equal(2UL, appeared.WindowId);
    }

    [Fact]
    public void NoChange_EmitsNoEvents()
    {
        WindowIdentityRegistry registry = new WindowIdentityRegistry();
        registry.Diff(new[] { Win(0x100, "a") });
        Assert.Empty(registry.Diff(new[] { Win(0x100, "a") }));
    }
}
