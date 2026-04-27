using WindowStream.Core.Capture;
using WindowStream.Core.Encode;

namespace WindowStream.Cli.Commands;

public sealed record WorkerArguments(
    WindowHandle Hwnd,
    int StreamId,
    string PipeName,
    EncoderOptions EncoderOptions);
