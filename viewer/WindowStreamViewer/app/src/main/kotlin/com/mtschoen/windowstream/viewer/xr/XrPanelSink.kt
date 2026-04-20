package com.mtschoen.windowstream.viewer.xr

import android.view.Surface
import com.mtschoen.windowstream.viewer.decoder.FrameSink
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.runBlocking

class XrPanelSink : FrameSink {
    private val pendingSurfaceChannel: Channel<Surface> = Channel(Channel.RENDEZVOUS)
    private val frameCountState = MutableStateFlow(0L)
    val framesRendered: StateFlow<Long> = frameCountState.asStateFlow()
    private val dimensionsState = MutableStateFlow<Pair<Int, Int>?>(null)
    val dimensions: StateFlow<Pair<Int, Int>?> = dimensionsState.asStateFlow()

    fun provideSurfaceFromXrSystem(surface: Surface) {
        runBlocking { pendingSurfaceChannel.send(surface) }
    }

    override fun acquireSurface(width: Int, height: Int): Surface {
        dimensionsState.value = width to height
        return runBlocking { pendingSurfaceChannel.receive() }
    }

    override fun releaseSurface() {
        dimensionsState.value = null
    }

    override fun onFrameRendered(presentationTimestampMicroseconds: Long) {
        frameCountState.value += 1
    }
}
