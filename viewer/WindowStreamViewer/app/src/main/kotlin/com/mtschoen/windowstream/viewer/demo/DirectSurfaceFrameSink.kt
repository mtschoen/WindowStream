package com.mtschoen.windowstream.viewer.demo

import android.view.Surface
import com.mtschoen.windowstream.viewer.decoder.FrameSink
import java.util.concurrent.atomic.AtomicLong

/**
 * Phone-compatible FrameSink that wraps a pre-acquired [Surface] (typically the
 * surface from a SurfaceView). Bypasses XR runtime requirements so the viewer can
 * be demoed on a plain Android emulator or handset.
 */
class DirectSurfaceFrameSink(private val surface: Surface) : FrameSink {
    private val frameCountAtomic = AtomicLong()
    val framesRenderedCount: Long get() = frameCountAtomic.get()

    override fun acquireSurface(width: Int, height: Int): Surface = surface

    override fun releaseSurface() {
        // Lifetime of the Surface is owned by the SurfaceView; nothing to do here.
    }

    override fun onFrameRendered(presentationTimestampMicroseconds: Long) {
        frameCountAtomic.incrementAndGet()
    }
}
