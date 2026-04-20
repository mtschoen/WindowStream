#if WINDOWS
namespace WindowStream.Integration.Tests.Loopback;

/// <summary>
/// Represents a single decoded video frame produced by the <see cref="SessionHostLoopbackHarness"/>.
/// </summary>
internal sealed record DecodedVideoFrame(int WidthPixels, int HeightPixels, bool IsKeyframe);
#endif
