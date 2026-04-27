namespace WindowStream.Core.Protocol;

public sealed record ResumeStreamMessage(int StreamId) : ControlMessage;
