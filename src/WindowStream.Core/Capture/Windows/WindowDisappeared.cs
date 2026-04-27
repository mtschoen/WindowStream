namespace WindowStream.Core.Capture.Windows;

public sealed record WindowDisappeared(ulong WindowId) : WindowEnumerationEvent;
