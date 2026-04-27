using System.Collections.Generic;

namespace WindowStream.Core.Capture.Windows;

public sealed class WindowIdentityRegistry
{
    private readonly Dictionary<long, KnownWindow> handleToKnown = new();
    private ulong nextWindowId = 1;

    public IEnumerable<WindowEnumerationEvent> Diff(IReadOnlyList<WindowInformation> currentSnapshot)
    {
        HashSet<long> seenHandles = new HashSet<long>();
        List<WindowEnumerationEvent> events = new List<WindowEnumerationEvent>();

        foreach (WindowInformation current in currentSnapshot)
        {
            long handle = current.handle.value;
            seenHandles.Add(handle);
            if (handleToKnown.TryGetValue(handle, out KnownWindow? previous))
            {
                bool titleChanged = previous.Title != current.title;
                bool widthChanged = previous.WidthPixels != current.widthPixels;
                bool heightChanged = previous.HeightPixels != current.heightPixels;
                if (titleChanged || widthChanged || heightChanged)
                {
                    events.Add(new WindowChanged(
                        previous.WindowId,
                        titleChanged ? current.title : null,
                        widthChanged ? current.widthPixels : null,
                        heightChanged ? current.heightPixels : null));
                    handleToKnown[handle] = previous with
                    {
                        Title = current.title,
                        WidthPixels = current.widthPixels,
                        HeightPixels = current.heightPixels
                    };
                }
            }
            else
            {
                ulong assigned = nextWindowId++;
                handleToKnown[handle] = new KnownWindow(
                    assigned, current.title, current.widthPixels, current.heightPixels);
                events.Add(new WindowAppeared(assigned, current));
            }
        }

        List<long> goneHandles = new List<long>();
        foreach (KeyValuePair<long, KnownWindow> entry in handleToKnown)
        {
            if (!seenHandles.Contains(entry.Key))
            {
                goneHandles.Add(entry.Key);
            }
        }
        foreach (long gone in goneHandles)
        {
            ulong identifier = handleToKnown[gone].WindowId;
            handleToKnown.Remove(gone);
            events.Add(new WindowDisappeared(identifier));
        }

        return events;
    }

    private sealed record KnownWindow(ulong WindowId, string Title, int WidthPixels, int HeightPixels);
}
