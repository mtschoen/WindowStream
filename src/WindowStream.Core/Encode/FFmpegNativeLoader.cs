using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using FFmpeg.AutoGen;

namespace WindowStream.Core.Encode;

[ExcludeFromCodeCoverage(Justification = "Delegates entirely to native FFmpeg; covered by Phase 12 integration tests.")]
public sealed class FFmpegNativeLoader : IFFmpegNativeLoader
{
    private static readonly object synchronizationLock = new object();
    private static bool initialized;

    public void EnsureLoaded()
    {
        lock (synchronizationLock)
        {
            if (initialized)
            {
                return;
            }
            string binaryDirectory = Path.GetDirectoryName(typeof(FFmpegNativeLoader).Assembly.Location)
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
            initialized = true;
        }
    }
}
