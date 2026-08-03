package com.mtschoen.windowstream.viewer.demo

import com.mtschoen.windowstream.viewer.control.WindowDescriptor
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Test

class DemoStreamSelectionTest {
    private val windows = listOf(
        window(windowId = 10uL, hwnd = 100L),
        window(windowId = 20uL, hwnd = 200L),
        window(windowId = 30uL, hwnd = 300L)
    )

    @Test
    fun `selected HWNDs determine stream count and resolve to window ids`() {
        val selection = DemoStreamSelection(
            selectedWindowIds = longArrayOf(),
            selectedWindowHwnds = longArrayOf(100L, 300L)
        )

        assertEquals(2, selection.streamCount)
        assertEquals(10uL, selection.resolveWindowId(0, windows))
        assertEquals(30uL, selection.resolveWindowId(1, windows))
    }

    @Test
    fun `selected window ids are used when HWNDs are absent`() {
        val selection = DemoStreamSelection(
            selectedWindowIds = longArrayOf(20L, 30L),
            selectedWindowHwnds = longArrayOf()
        )

        assertEquals(2, selection.streamCount)
        assertEquals(20uL, selection.resolveWindowId(0, windows))
        assertEquals(30uL, selection.resolveWindowId(1, windows))
    }

    @Test
    fun `empty selection opens the first advertised window`() {
        val selection = DemoStreamSelection(longArrayOf(), longArrayOf())

        assertEquals(1, selection.streamCount)
        assertEquals(10uL, selection.resolveWindowId(0, windows))
    }

    @Test
    fun `unknown HWND reports the available catalogue`() {
        val selection = DemoStreamSelection(longArrayOf(), longArrayOf(999L))

        val exception = assertThrows(IllegalStateException::class.java) {
            selection.resolveWindowId(0, windows)
        }

        assertEquals(
            "no window in ServerHello with hwnd=999; available hwnds=[100, 200, 300]",
            exception.message
        )
    }

    @Test
    fun `empty selection rejects an empty server catalogue`() {
        val selection = DemoStreamSelection(longArrayOf(), longArrayOf())

        val exception = assertThrows(IllegalStateException::class.java) {
            selection.resolveWindowId(0, emptyList())
        }

        assertEquals("server advertised no windows in ServerHello", exception.message)
    }

    private fun window(windowId: ULong, hwnd: Long) = WindowDescriptor(
        windowId = windowId,
        hwnd = hwnd,
        processId = 1,
        processName = "test.exe",
        title = "Test",
        physicalWidth = 1920,
        physicalHeight = 1080
    )
}
