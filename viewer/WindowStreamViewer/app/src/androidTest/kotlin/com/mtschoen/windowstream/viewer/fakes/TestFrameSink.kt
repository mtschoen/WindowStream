package com.mtschoen.windowstream.viewer.fakes

import android.view.Surface
import com.mtschoen.windowstream.viewer.decoder.FrameSink
import java.util.concurrent.atomic.AtomicLong

/**
 * Integration-test [FrameSink] that counts decoded frames.
 *
 * [acquireSurface] is not called when [com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder]
 * is constructed with `outputToSurface = false`. In that mode decoded frames arrive as raw
 * buffer callbacks and [onFrameRendered] is invoked directly from
 * [android.media.MediaCodec.Callback.onOutputBufferAvailable], so this sink only needs to count
 * calls to [onFrameRendered].
 *
 * [acquireSurface] and [releaseSurface] are implemented as no-ops / throw-safety guards so that
 * this class satisfies the [FrameSink] interface regardless of which decoder mode is chosen.
 */
class TestFrameSink : FrameSink {
    val framesRenderedCount: AtomicLong = AtomicLong(0)

    override fun acquireSurface(width: Int, height: Int): Surface {
        throw UnsupportedOperationException(
            "TestFrameSink.acquireSurface() must not be called when outputToSurface = false"
        )
    }

    override fun releaseSurface() {
        // No-op: no surface was acquired.
    }

    override fun onFrameRendered(presentationTimestampMicroseconds: Long) {
        framesRenderedCount.incrementAndGet()
    }
}
