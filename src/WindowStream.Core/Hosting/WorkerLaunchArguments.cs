namespace WindowStream.Core.Hosting;

public sealed record WorkerLaunchArguments(
    long Hwnd,
    int StreamId,
    string PipeName,
    string EncoderOptionsJson);
