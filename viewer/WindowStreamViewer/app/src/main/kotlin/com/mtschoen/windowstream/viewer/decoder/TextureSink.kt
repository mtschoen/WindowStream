package com.mtschoen.windowstream.viewer.decoder

import android.graphics.SurfaceTexture
import android.view.Surface
import java.util.concurrent.atomic.AtomicLong

class TextureSink : FrameSink {
    private var surfaceTexture: SurfaceTexture? = null
    private var surface: Surface? = null
    val framesRenderedCount: AtomicLong = AtomicLong(0)

    override fun acquireSurface(width: Int, height: Int): Surface {
        val texture = SurfaceTexture(0)
        texture.setDefaultBufferSize(width, height)
        texture.detachFromGLContext()
        val newSurface = Surface(texture)
        surfaceTexture = texture
        surface = newSurface
        return newSurface
    }

    override fun releaseSurface() {
        surface?.release()
        surfaceTexture?.release()
        surface = null
        surfaceTexture = null
    }

    override fun onFrameRendered(presentationTimestampMicroseconds: Long) {
        framesRenderedCount.incrementAndGet()
    }
}
