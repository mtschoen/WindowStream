package com.mtschoen.windowstream.viewer.xr

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class SpatialPanelLayoutTest {

    @Test
    fun `clampScale leaves an in-range value unchanged`() {
        assertEquals(1.0f, SpatialPanelLayout.clampScale(1.0f))
    }

    @Test
    fun `clampScale floors a too-small value at MIN_SCALE`() {
        assertEquals(SpatialPanelLayout.MIN_SCALE, SpatialPanelLayout.clampScale(0.1f))
    }

    @Test
    fun `clampScale caps a too-large value at MAX_SCALE`() {
        assertEquals(SpatialPanelLayout.MAX_SCALE, SpatialPanelLayout.clampScale(99.0f))
    }

    @Test
    fun `single panel is centered`() {
        assertEquals(0.0f, SpatialPanelLayout.computeSlotOffsetMeters(index = 0, count = 1))
    }

    @Test
    fun `two panels fan symmetrically around zero`() {
        val spacing = SpatialPanelLayout.SLOT_SPACING_METERS
        assertEquals(-spacing / 2f, SpatialPanelLayout.computeSlotOffsetMeters(0, 2))
        assertEquals(spacing / 2f, SpatialPanelLayout.computeSlotOffsetMeters(1, 2))
    }

    @Test
    fun `three panels place the middle one at the origin`() {
        val spacing = SpatialPanelLayout.SLOT_SPACING_METERS
        assertEquals(-spacing, SpatialPanelLayout.computeSlotOffsetMeters(0, 3))
        assertEquals(0.0f, SpatialPanelLayout.computeSlotOffsetMeters(1, 3))
        assertEquals(spacing, SpatialPanelLayout.computeSlotOffsetMeters(2, 3))
    }

    @Test
    fun `non-positive count is rejected`() {
        assertThrows(IllegalArgumentException::class.java) {
            SpatialPanelLayout.computeSlotOffsetMeters(index = 0, count = 0)
        }
    }

    @Test
    fun `index out of range is rejected`() {
        assertThrows(IllegalArgumentException::class.java) {
            SpatialPanelLayout.computeSlotOffsetMeters(index = 3, count = 2)
        }
    }

    @Test
    fun `negative index is rejected`() {
        assertThrows(IllegalArgumentException::class.java) {
            SpatialPanelLayout.computeSlotOffsetMeters(index = -1, count = 2)
        }
    }

    @Test
    fun `scale step and bounds are sane constants`() {
        assertTrue(SpatialPanelLayout.MIN_SCALE < SpatialPanelLayout.MAX_SCALE)
        assertTrue(SpatialPanelLayout.SCALE_STEP > 0f)
        assertTrue(SpatialPanelLayout.SLOT_SPACING_METERS > 0f)
    }
}
