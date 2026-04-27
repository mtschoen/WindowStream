namespace WindowStream.Core.Capture.Windows;

public sealed record WindowChanged(
    ulong WindowId,
    string? NewTitle,
    int? NewWidthPixels,
    int? NewHeightPixels) : WindowEnumerationEvent;
