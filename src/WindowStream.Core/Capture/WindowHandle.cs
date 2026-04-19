namespace WindowStream.Core.Capture;

public readonly record struct WindowHandle(long value)
{
    public override string ToString() => "0x" + value.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
}
