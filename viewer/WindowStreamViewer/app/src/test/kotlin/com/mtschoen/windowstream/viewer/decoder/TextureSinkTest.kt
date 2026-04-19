package com.mtschoen.windowstream.viewer.decoder

import android.graphics.SurfaceTexture
import android.view.Surface
import io.mockk.every
import io.mockk.justRun
import io.mockk.mockkConstructor
import io.mockk.unmockkConstructor
import io.mockk.verify
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test

class TextureSinkTest {

    @BeforeEach
    fun setUp() {
        mockkConstructor(SurfaceTexture::class)
        mockkConstructor(Surface::class)
        justRun { anyConstructed<SurfaceTexture>().setDefaultBufferSize(any(), any()) }
        justRun { anyConstructed<SurfaceTexture>().detachFromGLContext() }
        justRun { anyConstructed<SurfaceTexture>().release() }
        justRun { anyConstructed<Surface>().release() }
    }

    @AfterEach
    fun tearDown() {
        unmockkConstructor(SurfaceTexture::class)
        unmockkConstructor(Surface::class)
    }

    @Test
    fun `acquireSurface creates surface texture with given dimensions`() {
        val sink = TextureSink()

        val surface = sink.acquireSurface(1920, 1080)

        assertNotNull(surface)
        verify { anyConstructed<SurfaceTexture>().setDefaultBufferSize(1920, 1080) }
        verify { anyConstructed<SurfaceTexture>().detachFromGLContext() }
    }

    @Test
    fun `acquireSurface returns new surface each time`() {
        val sink = TextureSink()

        val firstSurface = sink.acquireSurface(640, 480)
        val secondSurface = sink.acquireSurface(1280, 720)

        assertNotNull(firstSurface)
        assertNotNull(secondSurface)
    }

    @Test
    fun `releaseSurface releases underlying resources`() {
        val sink = TextureSink()
        sink.acquireSurface(1920, 1080)

        sink.releaseSurface()

        verify { anyConstructed<Surface>().release() }
        verify { anyConstructed<SurfaceTexture>().release() }
    }

    @Test
    fun `releaseSurface is safe to call without prior acquireSurface`() {
        val sink = TextureSink()

        sink.releaseSurface()

        // No exception should be thrown, no Surface or SurfaceTexture constructed
        verify(exactly = 0) { anyConstructed<Surface>().release() }
        verify(exactly = 0) { anyConstructed<SurfaceTexture>().release() }
    }

    @Test
    fun `onFrameRendered increments framesRenderedCount`() {
        val sink = TextureSink()
        assertEquals(0L, sink.framesRenderedCount.get())

        sink.onFrameRendered(1000L)
        assertEquals(1L, sink.framesRenderedCount.get())

        sink.onFrameRendered(2000L)
        assertEquals(2L, sink.framesRenderedCount.get())
    }

    @Test
    fun `onFrameRendered increments count regardless of timestamp`() {
        val sink = TextureSink()

        sink.onFrameRendered(0L)
        sink.onFrameRendered(Long.MAX_VALUE)
        sink.onFrameRendered(-1L)

        assertEquals(3L, sink.framesRenderedCount.get())
    }

    @Test
    fun `framesRenderedCount starts at zero`() {
        val sink = TextureSink()

        assertEquals(0L, sink.framesRenderedCount.get())
    }
}
