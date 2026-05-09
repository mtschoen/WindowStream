namespace WindowStream.Core.Encode;

/// <summary>
/// Source of NV12 D3D11 textures for the GPU-resident pipeline. The encoder
/// implements this against its FFmpeg <c>hw_frames_ctx</c> pool; the capture
/// path's converter writes into the textures the pool hands out, then the
/// encoder consumes the matching AVFrame on the next <c>EncodeAsync</c>.
///
/// Acquire and Encode must be called in matching order — the pool internally
/// queues the AVFrame for each acquired texture; <c>EncodeAsync</c> dequeues
/// it. This matches the natural per-frame flow:
/// capture-converter Acquire → fill → encoder.EncodeAsync(CapturedFrame).
/// </summary>
public interface IFrameTexturePool
{
    /// <summary>
    /// Acquire one NV12 texture from the pool. The returned pointer is an
    /// <c>ID3D11Texture2D*</c> with format <c>DXGI_FORMAT_NV12</c> and
    /// dimensions matching the encoder configuration. The
    /// <paramref name="textureSubresourceIndex"/> is the subresource index
    /// (typically 0; FFmpeg's D3D11VA pool uses texture arrays so this can
    /// be non-zero in practice). The texture is owned by the pool and will
    /// be reused after the matching <c>EncodeAsync</c> completes.
    /// </summary>
    void AcquireFrameTexture(out nint texturePointer, out int textureSubresourceIndex);
}
