package com.mtschoen.windowstream.viewer.xr

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class PanelDimensionsTest {

    @Test
    fun `1920x1080 input yields default width and 16x9 height`() {
        val (widthMeters, heightMeters) = computePanelDimensionsMeters(1920 to 1080)

        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS, widthMeters)
        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS * 9f / 16f, heightMeters, 0.001f)
    }

    @Test
    fun `3840x2160 4K input preserves 16x9 aspect ratio`() {
        val (widthMeters, heightMeters) = computePanelDimensionsMeters(3840 to 2160)

        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS, widthMeters)
        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS * 9f / 16f, heightMeters, 0.001f)
    }

    @Test
    fun `null dimensions fall back to 1920x1080 default`() {
        val (widthMeters, heightMeters) = computePanelDimensionsMeters(null)

        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS, widthMeters)
        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS * 9f / 16f, heightMeters, 0.001f)
    }

    @Test
    fun `square input yields equal width and height meters`() {
        val (widthMeters, heightMeters) = computePanelDimensionsMeters(1000 to 1000)

        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS, widthMeters)
        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS, heightMeters, 0.001f)
    }

    @Test
    fun `wide portrait input yields taller panel`() {
        // 1:2 aspect ratio (width < height)
        val (widthMeters, heightMeters) = computePanelDimensionsMeters(540 to 1080)

        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS, widthMeters)
        assertEquals(PanelPlacement.DEFAULT_WIDTH_METERS * 2f, heightMeters, 0.001f)
    }
}
