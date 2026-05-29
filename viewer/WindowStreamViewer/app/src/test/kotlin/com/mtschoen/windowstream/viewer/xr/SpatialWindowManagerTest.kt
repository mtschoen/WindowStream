package com.mtschoen.windowstream.viewer.xr

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class SpatialWindowManagerTest {

    private fun manager() = SpatialWindowManager()

    private fun SpatialWindowManager.add(windowId: ULong, streamId: Int = windowId.toInt()) =
        addPanel(
            windowId = windowId,
            streamId = streamId,
            title = "Window $windowId",
            contentWidthPixels = 1920,
            contentHeightPixels = 1080,
        )

    @Test
    fun `starts empty`() {
        val manager = manager()
        assertTrue(manager.panels.value.isEmpty())
        assertTrue(manager.openWindowIds.value.isEmpty())
        assertFalse(manager.isOpen(1uL))
    }

    @Test
    fun `addPanel adds a panel and marks the window open`() {
        val manager = manager()
        manager.add(7uL, streamId = 42)

        val panel = manager.panels.value.single()
        assertEquals(7uL, panel.windowId)
        assertEquals(42, panel.streamId)
        assertEquals("Window 7", panel.title)
        assertEquals(1920, panel.contentWidthPixels)
        assertFalse(panel.minimized)
        assertEquals(1.0f, panel.scale)
        assertTrue(manager.isOpen(7uL))
        assertEquals(setOf(7uL), manager.openWindowIds.value)
    }

    @Test
    fun `addPanel is idempotent per window`() {
        val manager = manager()
        manager.add(7uL, streamId = 1)
        manager.add(7uL, streamId = 2)

        assertEquals(1, manager.panels.value.size)
        assertEquals(1, manager.panels.value.single().streamId)
    }

    @Test
    fun `addPanel preserves spawn order across windows`() {
        val manager = manager()
        manager.add(1uL)
        manager.add(2uL)
        manager.add(3uL)

        assertEquals(listOf(1uL, 2uL, 3uL), manager.panels.value.map { it.windowId })
        assertEquals(setOf(1uL, 2uL, 3uL), manager.openWindowIds.value)
    }

    @Test
    fun `removePanel removes an existing panel`() {
        val manager = manager()
        manager.add(1uL)
        manager.add(2uL)

        manager.removePanel(1uL)

        assertEquals(listOf(2uL), manager.panels.value.map { it.windowId })
        assertEquals(setOf(2uL), manager.openWindowIds.value)
        assertFalse(manager.isOpen(1uL))
    }

    @Test
    fun `removePanel for an unknown window is a no-op`() {
        val manager = manager()
        manager.add(1uL)

        manager.removePanel(99uL)

        assertEquals(listOf(1uL), manager.panels.value.map { it.windowId })
    }

    @Test
    fun `toggleMinimize flips the flag and returns the new value`() {
        val manager = manager()
        manager.add(1uL)

        assertEquals(true, manager.toggleMinimize(1uL))
        assertTrue(manager.panels.value.single().minimized)

        assertEquals(false, manager.toggleMinimize(1uL))
        assertFalse(manager.panels.value.single().minimized)
    }

    @Test
    fun `toggleMinimize on an unknown window returns null and changes nothing`() {
        val manager = manager()
        manager.add(1uL)

        assertNull(manager.toggleMinimize(99uL))
        assertFalse(manager.panels.value.single().minimized)
    }

    @Test
    fun `adjustScale changes only the matching panel`() {
        val manager = manager()
        manager.add(1uL)
        manager.add(2uL)

        manager.adjustScale(1uL, SpatialPanelLayout.SCALE_STEP)

        val byId = manager.panels.value.associateBy { it.windowId }
        assertEquals(1.0f + SpatialPanelLayout.SCALE_STEP, byId.getValue(1uL).scale)
        assertEquals(1.0f, byId.getValue(2uL).scale)
    }

    @Test
    fun `adjustScale clamps at the bounds`() {
        val manager = manager()
        manager.add(1uL)

        manager.adjustScale(1uL, 100f)
        assertEquals(SpatialPanelLayout.MAX_SCALE, manager.panels.value.single().scale)

        manager.adjustScale(1uL, -100f)
        assertEquals(SpatialPanelLayout.MIN_SCALE, manager.panels.value.single().scale)
    }

    @Test
    fun `adjustScale on an unknown window leaves panels untouched`() {
        val manager = manager()
        manager.add(1uL)

        manager.adjustScale(99uL, 1f)

        assertEquals(1.0f, manager.panels.value.single().scale)
    }

    @Test
    fun `updateDimensions updates only the matching panel`() {
        val manager = manager()
        manager.add(1uL)
        manager.add(2uL)

        manager.updateDimensions(1uL, 1280, 720)

        val byId = manager.panels.value.associateBy { it.windowId }
        assertEquals(1280, byId.getValue(1uL).contentWidthPixels)
        assertEquals(720, byId.getValue(1uL).contentHeightPixels)
        assertEquals(1920, byId.getValue(2uL).contentWidthPixels)
    }

    @Test
    fun `updateDimensions on an unknown window leaves panels untouched`() {
        val manager = manager()
        manager.add(1uL)

        manager.updateDimensions(99uL, 1280, 720)

        assertEquals(1920, manager.panels.value.single().contentWidthPixels)
    }
}
