package com.mtschoen.windowstream.viewer.xr

import android.view.Surface
import io.mockk.mockk
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Test
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class XrPanelSinkTest {

    /**
     * Sends a surface from a separate thread to simulate the XR system providing
     * a surface asynchronously. Delays slightly to ensure the receiver side has
     * started blocking before the send is attempted on the rendezvous channel.
     */
    private fun sendSurfaceAsync(sink: XrPanelSink, surface: Surface) {
        val executor = Executors.newSingleThreadExecutor()
        executor.submit {
            // Small delay so the acquireSurface caller reaches receive() first
            Thread.sleep(50)
            sink.provideSurfaceFromXrSystem(surface)
        }
        executor.shutdown()
    }

    @Test
    fun `framesRendered starts at zero`() {
        val sink = XrPanelSink()

        assertEquals(0L, sink.framesRendered.value)
    }

    @Test
    fun `onFrameRendered increments framesRendered`() {
        val sink = XrPanelSink()

        sink.onFrameRendered(1_000L)
        assertEquals(1L, sink.framesRendered.value)

        sink.onFrameRendered(2_000L)
        assertEquals(2L, sink.framesRendered.value)
    }

    @Test
    fun `onFrameRendered increments regardless of timestamp value`() {
        val sink = XrPanelSink()

        sink.onFrameRendered(0L)
        sink.onFrameRendered(Long.MAX_VALUE)
        sink.onFrameRendered(-1L)

        assertEquals(3L, sink.framesRendered.value)
    }

    @Test
    fun `dimensions starts as null`() {
        val sink = XrPanelSink()

        assertNull(sink.dimensions.value)
    }

    @Test
    fun `acquireSurface sets dimensions and returns surface provided by XR system`() {
        val sink = XrPanelSink()
        val fakeSurface = mockk<Surface>()

        sendSurfaceAsync(sink, fakeSurface)
        val acquired = sink.acquireSurface(1920, 1080)

        assertEquals(1920 to 1080, sink.dimensions.value)
        assertSame(fakeSurface, acquired)
    }

    @Test
    fun `acquireSurface records the requested dimensions`() {
        val sink = XrPanelSink()
        val fakeSurface = mockk<Surface>()

        sendSurfaceAsync(sink, fakeSurface)
        sink.acquireSurface(3840, 2160)

        assertEquals(3840 to 2160, sink.dimensions.value)
    }

    @Test
    fun `releaseSurface clears dimensions`() {
        val sink = XrPanelSink()
        val fakeSurface = mockk<Surface>()

        sendSurfaceAsync(sink, fakeSurface)
        sink.acquireSurface(1280, 720)

        sink.releaseSurface()

        assertNull(sink.dimensions.value)
    }

    @Test
    fun `releaseSurface is safe to call without prior acquireSurface`() {
        val sink = XrPanelSink()

        sink.releaseSurface()

        assertNull(sink.dimensions.value)
    }
}
