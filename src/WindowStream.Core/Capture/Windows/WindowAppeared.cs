namespace WindowStream.Core.Capture.Windows;

public sealed record WindowAppeared(ulong WindowId, WindowInformation Information) : WindowEnumerationEvent;
