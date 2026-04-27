namespace WindowStream.Core.Protocol;

/// <summary>
/// Viewer → server keyboard input event. <see cref="StreamId"/> targets the
/// HWND of the corresponding stream; the server routes the synthesized keystroke
/// to that window. When <see cref="IsUnicode"/> is true, <see cref="KeyCode"/>
/// is a Unicode codepoint and the server emits KEYEVENTF_UNICODE via SendInput.
/// When false, <see cref="KeyCode"/> is a Windows virtual-key code (VK_*) and
/// the server emits a normal scan-code event.
/// </summary>
public sealed record KeyEventMessage(int StreamId, int KeyCode, bool IsUnicode, bool IsDown) : ControlMessage;
