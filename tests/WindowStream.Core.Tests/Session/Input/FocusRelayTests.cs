using System.Collections.Generic;
using WindowStream.Core.Session.Input;
using Xunit;

namespace WindowStream.Core.Tests.Session.Input;

public class FocusRelayTests
{
    private sealed class FakeForegroundApi : IForegroundWindowApi
    {
        public long Foreground { get; set; }
        public Dictionary<long, uint> HandleToThread { get; } = new();
        public List<string> Events { get; } = new();

        public long GetForegroundWindow() => Foreground;

        public uint GetWindowThreadProcessId(long hwnd) =>
            HandleToThread.TryGetValue(hwnd, out uint thread) ? thread : 0;

        public bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach)
        {
            Events.Add(attach
                ? $"attach({sourceThreadId}->{targetThreadId})"
                : $"detach({sourceThreadId}->{targetThreadId})");
            return true;
        }

        public bool SetForegroundWindow(long hwnd)
        {
            Events.Add($"setForeground({hwnd})");
            Foreground = hwnd;
            return true;
        }

        public uint CurrentThreadId() => 99;
    }

    [Fact]
    public void BringToForeground_RunsAttachDetachDance()
    {
        FakeForegroundApi api = new FakeForegroundApi
        {
            Foreground = 0x100,
            HandleToThread = { [0x100] = 10, [0x200] = 20 }
        };
        FocusRelay relay = new FocusRelay(api);

        bool result = relay.BringToForeground(0x200);

        Assert.True(result);
        Assert.Equal(new[]
        {
            "attach(99->10)",
            "setForeground(512)",
            "detach(99->10)"
        }, api.Events.ToArray());
    }

    [Fact]
    public void BringToForeground_NoOpIfAlreadyForeground()
    {
        FakeForegroundApi api = new FakeForegroundApi
        {
            Foreground = 0x100,
            HandleToThread = { [0x100] = 10 }
        };
        FocusRelay relay = new FocusRelay(api);

        bool result = relay.BringToForeground(0x100);

        Assert.True(result);
        Assert.Empty(api.Events);
    }
}
