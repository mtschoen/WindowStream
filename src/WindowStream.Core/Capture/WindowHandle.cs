using System.Globalization;

namespace WindowStream.Core.Capture;

public readonly record struct WindowHandle(long Value)
{
    public override string ToString() => "0x" + Value.ToString("X", CultureInfo.InvariantCulture);
}
