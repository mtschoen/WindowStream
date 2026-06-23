namespace WindowStream.Core.Protocol;

/// <summary>
/// Viewer → server pointer/mouse input event. Coordinates are normalized to [0, 1]
/// relative to the stream's content area so the server can scale to the actual window
/// dimensions. <see cref="StreamId"/> routes the event to the correct window.
/// </summary>
public sealed record MouseEventMessage(
    int StreamId,
    float NormalizedX,
    float NormalizedY,
    MouseEventType EventType,
    int ButtonFlags,
    int ScrollDelta) : ControlMessage;

/// <summary>
/// Classifies the pointer action. Matches the mapping from Android <c>MotionEvent</c>
/// action constants to Win32 <c>MOUSEINPUT</c> flags.
/// </summary>
public enum MouseEventType
{
    Move = 0,
    ButtonDown = 1,
    ButtonUp = 2,
    Scroll = 3
}

/// <summary>
/// Bitmask for <see cref="MouseEventMessage.ButtonFlags"/>. Matches the Win32
/// <c>MOUSEEVENTF_*</c> button-event convention (left=1, right=2, middle=4).
/// </summary>
public static class MouseButton
{
    public const int Left = 1;
    public const int Right = 2;
    public const int Middle = 4;
}
