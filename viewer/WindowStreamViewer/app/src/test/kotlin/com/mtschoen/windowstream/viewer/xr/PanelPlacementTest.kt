package com.mtschoen.windowstream.viewer.xr

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class PanelPlacementTest {

    @Test
    fun `DEFAULT_DISTANCE_METERS is 1_5 meters`() {
        assertEquals(1.5f, PanelPlacement.DEFAULT_DISTANCE_METERS)
    }

    @Test
    fun `DEFAULT_HEIGHT_METERS is 1_6 meters representing eye height`() {
        assertEquals(1.6f, PanelPlacement.DEFAULT_HEIGHT_METERS)
    }

    @Test
    fun `DEFAULT_YAW_RADIANS is zero meaning straight ahead`() {
        assertEquals(0.0f, PanelPlacement.DEFAULT_YAW_RADIANS)
    }

    @Test
    fun `DEFAULT_PITCH_DEGREES is negative meaning slight downward tilt toward viewer`() {
        assertEquals(-6.0f, PanelPlacement.DEFAULT_PITCH_DEGREES)
    }

    @Test
    fun `DEFAULT_WIDTH_METERS is 1_2 meters`() {
        assertEquals(1.2f, PanelPlacement.DEFAULT_WIDTH_METERS)
    }

    @Test
    fun `DEFAULT_HEIGHT_METERS_PANEL is 0_675 meters representing 16x9 aspect ratio height`() {
        assertEquals(0.675f, PanelPlacement.DEFAULT_HEIGHT_METERS_PANEL)
    }

    @Test
    fun `panel aspect ratio matches 16x9`() {
        val ratio = PanelPlacement.DEFAULT_WIDTH_METERS / PanelPlacement.DEFAULT_HEIGHT_METERS_PANEL
        assertEquals(16.0f / 9.0f, ratio, 0.001f)
    }
}
