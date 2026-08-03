package com.mtschoen.windowstream.viewer.demo

import com.mtschoen.windowstream.viewer.control.WindowDescriptor

/**
 * Resolves adb-selected HWNDs or protocol window IDs against a server catalogue.
 */
internal class DemoStreamSelection(
    private val selectedWindowIds: LongArray,
    private val selectedWindowHwnds: LongArray
) {
    val streamCount: Int = when {
        selectedWindowHwnds.isNotEmpty() -> selectedWindowHwnds.size
        selectedWindowIds.isNotEmpty() -> selectedWindowIds.size
        else -> 1
    }

    fun resolveWindowId(streamIndex: Int, windows: List<WindowDescriptor>): ULong {
        if (selectedWindowHwnds.isNotEmpty()) {
            val targetHwnd = selectedWindowHwnds[streamIndex]
            return windows.firstOrNull { descriptor -> descriptor.hwnd == targetHwnd }
                ?.windowId
                ?: error(
                    "no window in ServerHello with hwnd=$targetHwnd; " +
                        "available hwnds=${windows.map { it.hwnd }}"
                )
        }
        if (selectedWindowIds.isNotEmpty()) {
            return selectedWindowIds[streamIndex].toULong()
        }
        return (windows.firstOrNull()
            ?: error("server advertised no windows in ServerHello"))
            .windowId
    }
}
