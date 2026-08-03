package com.mtschoen.windowstream.viewer.demo

import android.os.Build
import android.view.SurfaceView
import io.mockk.mockk
import io.mockk.verify
import org.junit.jupiter.api.Test

class PanelSurfaceLifecycleTest {

    @Test
    fun `retains panel surface while view remains attached`() {
        val surfaceView = mockk<SurfaceView>(relaxed = true)

        retainPanelSurfaceWhileAttached(surfaceView, Build.VERSION_CODES.UPSIDE_DOWN_CAKE)

        verify(exactly = 1) {
            surfaceView.setSurfaceLifecycle(SurfaceView.SURFACE_LIFECYCLE_FOLLOWS_ATTACHMENT)
        }
    }

    @Test
    fun `keeps default surface lifecycle below Android 14`() {
        val surfaceView = mockk<SurfaceView>(relaxed = true)

        retainPanelSurfaceWhileAttached(surfaceView, Build.VERSION_CODES.TIRAMISU)

        verify(exactly = 0) {
            surfaceView.setSurfaceLifecycle(any())
        }
    }
}
