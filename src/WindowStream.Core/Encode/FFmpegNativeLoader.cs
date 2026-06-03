using System.Diagnostics.CodeAnalysis;
using FFmpeg.AutoGen;

namespace WindowStream.Core.Encode;

[ExcludeFromCodeCoverage(Justification = "Delegates entirely to native FFmpeg; covered by Phase 12 integration tests.")]
public sealed class FFmpegNativeLoader : IFFmpegNativeLoader
{
    static readonly object SynchronizationLock = new object();
    static bool _initialized;

    public void EnsureLoaded()
    {
        lock (SynchronizationLock)
        {
            if (_initialized)
            {
                return;
            }
            var binaryDirectory = Path.GetDirectoryName(typeof(FFmpegNativeLoader).Assembly.Location)
                                  ?? AppContext.BaseDirectory;
            ffmpeg.RootPath = binaryDirectory;
            try
            {
                // Probe a known function to force the native load
                _ = ffmpeg.av_version_info();
            }
            catch (Exception exception)
            {
                throw new EncoderException("Failed to load FFmpeg native libraries.", exception);
            }
            _initialized = true;
        }
    }
}
