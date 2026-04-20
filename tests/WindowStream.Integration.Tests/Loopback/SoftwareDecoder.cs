#if WINDOWS
using System;
using FFmpeg.AutoGen;

namespace WindowStream.Integration.Tests.Loopback;

/// <summary>
/// Wraps the FFmpeg software H.264 decoder (libavcodec) via FFmpeg.AutoGen.
/// Accepts raw NAL-unit byte arrays and returns the decoded frame dimensions.
///
/// The decoder is intentionally minimal: it only checks that the pixel dimensions are
/// consistent with the stream descriptor, which is sufficient to prove the pipeline is
/// delivering decodable content. Full pixel-value assertions are not required.
/// </summary>
internal sealed class SoftwareDecoder : IDisposable
{
    private unsafe AVCodecContext* codecContext;
    private unsafe AVPacket* reusablePacket;
    private unsafe AVFrame* reusableFrame;
    private bool disposed;

    internal SoftwareDecoder()
    {
        unsafe
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null) throw new InvalidOperationException("libavcodec H.264 decoder not available.");
            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null) throw new InvalidOperationException("avcodec_alloc_context3 returned null.");
            int openResult = ffmpeg.avcodec_open2(codecContext, codec, null);
            if (openResult < 0) throw new InvalidOperationException($"avcodec_open2 failed: {openResult}");
            reusablePacket = ffmpeg.av_packet_alloc();
            reusableFrame = ffmpeg.av_frame_alloc();
        }
    }

    /// <summary>
    /// Attempts to decode a single NAL unit.
    /// Returns <c>true</c> and the decoded width/height if the decoder produced a frame;
    /// returns <c>false</c> if the decoder buffered the data for a future frame.
    /// </summary>
    internal bool TryDecode(byte[] nalUnit, out int widthPixels, out int heightPixels)
    {
        widthPixels = 0;
        heightPixels = 0;

        unsafe
        {
            fixed (byte* data = nalUnit)
            {
                reusablePacket->data = data;
                reusablePacket->size = nalUnit.Length;
                int sendResult = ffmpeg.avcodec_send_packet(codecContext, reusablePacket);
                if (sendResult < 0) return false;
            }

            int receiveResult = ffmpeg.avcodec_receive_frame(codecContext, reusableFrame);
            if (receiveResult < 0) return false;

            widthPixels = reusableFrame->width;
            heightPixels = reusableFrame->height;
            ffmpeg.av_frame_unref(reusableFrame);
            return true;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        unsafe
        {
            if (reusablePacket != null)
            {
                AVPacket* packet = reusablePacket;
                ffmpeg.av_packet_free(&packet);
                reusablePacket = null;
            }
            if (reusableFrame != null)
            {
                AVFrame* frame = reusableFrame;
                ffmpeg.av_frame_free(&frame);
                reusableFrame = null;
            }
            if (codecContext != null)
            {
                AVCodecContext* context = codecContext;
                ffmpeg.avcodec_free_context(&context);
                codecContext = null;
            }
        }
    }
}
#endif
