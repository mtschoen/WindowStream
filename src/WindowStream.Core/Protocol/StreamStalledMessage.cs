using WindowStream.Core.Capture.Detection;

namespace WindowStream.Core.Protocol;

public sealed record StreamStalledMessage(int StreamId, StallCause Cause) : ControlMessage;
