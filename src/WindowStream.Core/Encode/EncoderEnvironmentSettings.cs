namespace WindowStream.Core.Encode;

sealed record EncoderEnvironmentSettings(
    int FramesPerSecond,
    int GroupOfPicturesLength,
    string Tune,
    int SurfaceCount)
{
    const int MaximumFramesPerSecond = 240;
    const int MaximumGroupOfPicturesLength = 600;
    const int MaximumSurfaceCount = 64;

    public static EncoderEnvironmentSettings Load(
        Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);

        var tune = readEnvironmentVariable("WINDOWSTREAM_NVENC_TUNE");
        return new EncoderEnvironmentSettings(
            FramesPerSecond: ReadPositiveInteger(
                readEnvironmentVariable("WINDOWSTREAM_NVENC_FPS"), 60, MaximumFramesPerSecond),
            GroupOfPicturesLength: ReadPositiveInteger(
                readEnvironmentVariable("WINDOWSTREAM_NVENC_GOP"), 30, MaximumGroupOfPicturesLength),
            Tune: string.IsNullOrWhiteSpace(tune) ? "ull" : tune,
            SurfaceCount: ReadPositiveInteger(
                readEnvironmentVariable("WINDOWSTREAM_NVENC_SURFACES"), 1, MaximumSurfaceCount));
    }

    static int ReadPositiveInteger(string? value, int defaultValue, int maximumValue) =>
        int.TryParse(value, out var parsedValue) &&
        parsedValue >= 1 &&
        parsedValue <= maximumValue
            ? parsedValue
            : defaultValue;
}
