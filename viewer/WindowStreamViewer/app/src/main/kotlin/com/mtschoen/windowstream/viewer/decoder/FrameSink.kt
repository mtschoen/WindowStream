package com.mtschoen.windowstream.viewer.decoder

import android.view.Surface

interface FrameSink {
    fun acquireSurface(width: Int, height: Int): Surface
    fun releaseSurface()
    fun onFrameRendered(presentationTimestampMicroseconds: Long)
}
