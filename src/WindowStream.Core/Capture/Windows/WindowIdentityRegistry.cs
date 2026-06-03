namespace WindowStream.Core.Capture.Windows;

public sealed class WindowIdentityRegistry
{
    readonly Dictionary<long, KnownWindow> _handleToKnown = new();
    ulong _nextWindowId = 1;

    public IEnumerable<WindowEnumerationEvent> Diff(IReadOnlyList<WindowInformation> currentSnapshot)
    {
        var seenHandles = new HashSet<long>();
        var events = new List<WindowEnumerationEvent>();

        foreach (var current in currentSnapshot)
        {
            var handle = current.Handle.Value;
            seenHandles.Add(handle);
            if (_handleToKnown.TryGetValue(handle, out var previous))
            {
                var titleChanged = previous.Title != current.Title;
                var widthChanged = previous.WidthPixels != current.WidthPixels;
                var heightChanged = previous.HeightPixels != current.HeightPixels;
                if (titleChanged || widthChanged || heightChanged)
                {
                    events.Add(new WindowChanged(
                        previous.WindowId,
                        titleChanged ? current.Title : null,
                        widthChanged ? current.WidthPixels : null,
                        heightChanged ? current.HeightPixels : null));
                    _handleToKnown[handle] = previous with
                    {
                        Title = current.Title,
                        WidthPixels = current.WidthPixels,
                        HeightPixels = current.HeightPixels
                    };
                }
            }
            else
            {
                var assigned = _nextWindowId++;
                _handleToKnown[handle] = new KnownWindow(
                    assigned, current.Title, current.WidthPixels, current.HeightPixels);
                events.Add(new WindowAppeared(assigned, current));
            }
        }

        var goneHandles = new List<long>();
        foreach (var entry in _handleToKnown)
        {
            if (!seenHandles.Contains(entry.Key))
            {
                goneHandles.Add(entry.Key);
            }
        }
        foreach (var gone in goneHandles)
        {
            var identifier = _handleToKnown[gone].WindowId;
            _handleToKnown.Remove(gone);
            events.Add(new WindowDisappeared(identifier));
        }

        return events;
    }

    sealed record KnownWindow(ulong WindowId, string Title, int WidthPixels, int HeightPixels);
}
