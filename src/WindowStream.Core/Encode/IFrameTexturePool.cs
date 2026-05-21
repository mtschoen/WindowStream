namespace WindowStream.Core.Encode;

/// <summary>
/// Source of NV12 D3D11 textures for the GPU-resident pipeline. The encoder
/// implements this against its FFmpeg <c>hw_frames_ctx</c> pool; the capture
/// path's converter writes into the textures the pool hands out, then the
/// encoder consumes the matching AVFrame on the next <c>EncodeAsync</c>.
///
/// Acquire is paired with EITHER <c>EncodeAsync</c> OR
/// <see cref="ReleaseFrameTexture"/> — every acquired texture must be
/// returned to the pool exactly once via one of those two calls.
/// The pool uses a <c>(texturePointer, textureSubresourceIndex)</c> keyed
/// lookup, so acquire-vs-consume order is not constrained.
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
    /// be reused after the matching <c>EncodeAsync</c> or
    /// <see cref="ReleaseFrameTexture"/> completes.
    /// </summary>
    void AcquireFrameTexture(out nint texturePointer, out int textureSubresourceIndex);

    /// <summary>
    /// Return a previously acquired pool texture without encoding it.
    /// Used when the caller acquires a frame but chooses not to encode it
    /// (e.g. the worker pause-skip path). The matching AVFrame is freed
    /// and its pool slot becomes available for reuse.
    /// </summary>
    /// <param name="texturePointer">
    /// The texture pointer returned by a prior <see cref="AcquireFrameTexture"/>.
    /// </param>
    /// <param name="textureSubresourceIndex">
    /// The subresource index returned by the same prior call.
    /// </param>
    void ReleaseFrameTexture(nint texturePointer, int textureSubresourceIndex);
}
