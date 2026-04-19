using System;
using System.Collections.Generic;
using System.Threading;

namespace WindowStream.Core.Capture;

public interface IWindowCapture : IAsyncDisposable
{
    IAsyncEnumerable<CapturedFrame> Frames { get; }
    WindowHandle handle { get; }
    CaptureOptions options { get; }
}
