package com.mtschoen.windowstream.viewer.app.ui

import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.StreamStoppedReason
import com.mtschoen.windowstream.viewer.control.WindowDescriptor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import kotlin.time.Duration.Companion.seconds

/**
 * Unit tests for [WindowPickerViewModel].
 *
 * All async tests use [MutableSharedFlow] with `replay = 8` so events emitted before
 * the ViewModel's internal collectors start are still delivered. Each async test also
 * yields briefly after ViewModel creation before emitting so the launched coroutines
 * have a chance to reach their first `collect` suspension point.
 *
 * Exercises:
 * - Initial window catalogue from the snapshot
 * - WINDOW_ADDED / WINDOW_REMOVED / WINDOW_UPDATED live push events
 * - toggleSelection (add, remove, idempotent)
 * - selectedWindowIdsAsLongArray output shape
 * - Selection is cleaned up when a selected window is removed
 * - Non-catalogue messages are ignored without crashing
 */
class WindowPickerViewModelTest {

    private fun makeWindow(
        windowId: ULong,
        title: String = "Window $windowId",
        processName: String = "app",
        physicalWidth: Int = 1920,
        physicalHeight: Int = 1080
    ): WindowDescriptor = WindowDescriptor(
        windowId = windowId,
        hwnd = windowId.toLong(),
        processId = 1,
        processName = processName,
        title = title,
        physicalWidth = physicalWidth,
        physicalHeight = physicalHeight
    )

    /** Builds a ViewModel and yields briefly so its launched collectors can subscribe. */
    private suspend fun buildViewModel(
        initialWindows: List<WindowDescriptor>,
        incoming: MutableSharedFlow<ControlMessage>,
        scope: CoroutineScope
    ): WindowPickerViewModel {
        val viewModel = WindowPickerViewModel(
            initialWindows = initialWindows,
            incomingMessages = incoming,
            scope = scope
        )
        // Give the launched coroutines a chance to start collecting before the test emits.
        delay(50)
        return viewModel
    }

    @Test
    fun `initial catalogue is populated from snapshot`() {
        val windows = listOf(makeWindow(1u), makeWindow(2u))
        val incoming = MutableSharedFlow<ControlMessage>()
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = WindowPickerViewModel(
            initialWindows = windows,
            incomingMessages = incoming,
            scope = scope
        )

        val catalogue = viewModel.windowCatalogue.value
        assertEquals(2, catalogue.size)
        assertTrue(catalogue.containsKey(1u.toULong()))
        assertTrue(catalogue.containsKey(2u.toULong()))
    }

    @Test
    fun `empty initial catalogue is valid`() {
        val incoming = MutableSharedFlow<ControlMessage>()
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = WindowPickerViewModel(
            initialWindows = emptyList(),
            incomingMessages = incoming,
            scope = scope
        )

        assertTrue(viewModel.windowCatalogue.value.isEmpty())
        assertTrue(viewModel.selection.value.isEmpty())
    }

    @Test
    fun `WINDOW_ADDED appends to catalogue`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u)),
            incoming = incoming,
            scope = scope
        )

        incoming.emit(ControlMessage.WindowAdded(window = makeWindow(2u)))

        withTimeout(1.seconds) {
            while (viewModel.windowCatalogue.value.size < 2) delay(10)
        }
        assertTrue(viewModel.windowCatalogue.value.containsKey(2u.toULong()))
    }

    @Test
    fun `WINDOW_REMOVED drops window from catalogue`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u), makeWindow(2u)),
            incoming = incoming,
            scope = scope
        )

        incoming.emit(ControlMessage.WindowRemoved(windowId = 1u))

        withTimeout(1.seconds) {
            while (viewModel.windowCatalogue.value.size > 1) delay(10)
        }
        assertFalse(viewModel.windowCatalogue.value.containsKey(1u.toULong()))
        assertTrue(viewModel.windowCatalogue.value.containsKey(2u.toULong()))
    }

    @Test
    fun `WINDOW_REMOVED while selected also removes from selection`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u), makeWindow(2u)),
            incoming = incoming,
            scope = scope
        )

        viewModel.toggleSelection(1u)
        assertTrue(viewModel.selection.value.contains(1u.toULong()))

        incoming.emit(ControlMessage.WindowRemoved(windowId = 1u))

        withTimeout(1.seconds) {
            while (viewModel.selection.value.contains(1u.toULong())) delay(10)
        }
        assertFalse(viewModel.selection.value.contains(1u.toULong()))
    }

    @Test
    fun `WINDOW_UPDATED patches title in catalogue`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u, title = "Old Title")),
            incoming = incoming,
            scope = scope
        )

        incoming.emit(
            ControlMessage.WindowUpdated(windowId = 1u, title = "New Title")
        )

        withTimeout(1.seconds) {
            while (viewModel.windowCatalogue.value[1u.toULong()]?.title != "New Title") delay(10)
        }
        assertEquals("New Title", viewModel.windowCatalogue.value[1u.toULong()]?.title)
    }

    @Test
    fun `WINDOW_UPDATED patches dimensions in catalogue`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u, physicalWidth = 800, physicalHeight = 600)),
            incoming = incoming,
            scope = scope
        )

        incoming.emit(
            ControlMessage.WindowUpdated(windowId = 1u, physicalWidth = 1920, physicalHeight = 1080)
        )

        withTimeout(1.seconds) {
            while (viewModel.windowCatalogue.value[1u.toULong()]?.physicalWidth != 1920) delay(10)
        }
        assertEquals(1920, viewModel.windowCatalogue.value[1u.toULong()]?.physicalWidth)
        assertEquals(1080, viewModel.windowCatalogue.value[1u.toULong()]?.physicalHeight)
    }

    @Test
    fun `WINDOW_UPDATED with null fields leaves existing values unchanged`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u, title = "Orig", physicalWidth = 800, physicalHeight = 600)),
            incoming = incoming,
            scope = scope
        )

        // Emit an update with only title changed, width/height null → should remain 800×600.
        incoming.emit(ControlMessage.WindowUpdated(windowId = 1u, title = "Changed"))

        withTimeout(1.seconds) {
            while (viewModel.windowCatalogue.value[1u.toULong()]?.title != "Changed") delay(10)
        }
        val updated = viewModel.windowCatalogue.value[1u.toULong()]
        assertEquals("Changed", updated?.title)
        assertEquals(800, updated?.physicalWidth)
        assertEquals(600, updated?.physicalHeight)
    }

    @Test
    fun `WINDOW_UPDATED for unknown windowId is a no-op`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u, title = "Original")),
            incoming = incoming,
            scope = scope
        )

        incoming.emit(ControlMessage.WindowUpdated(windowId = 99u, title = "Ghost"))

        // Give the coroutine time to process; catalogue should remain unchanged.
        delay(150)
        assertEquals(1, viewModel.windowCatalogue.value.size)
        assertFalse(viewModel.windowCatalogue.value.containsKey(99u.toULong()))
        assertEquals("Original", viewModel.windowCatalogue.value[1u.toULong()]?.title)
    }

    @Test
    fun `toggleSelection adds windowId to selection`() {
        val incoming = MutableSharedFlow<ControlMessage>()
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = WindowPickerViewModel(
            initialWindows = listOf(makeWindow(5u)),
            incomingMessages = incoming,
            scope = scope
        )

        assertTrue(viewModel.selection.value.isEmpty())
        viewModel.toggleSelection(5u)
        assertTrue(viewModel.selection.value.contains(5u.toULong()))
    }

    @Test
    fun `toggleSelection removes windowId when already selected`() {
        val incoming = MutableSharedFlow<ControlMessage>()
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = WindowPickerViewModel(
            initialWindows = listOf(makeWindow(5u)),
            incomingMessages = incoming,
            scope = scope
        )

        viewModel.toggleSelection(5u)
        viewModel.toggleSelection(5u)
        assertFalse(viewModel.selection.value.contains(5u.toULong()))
    }

    @Test
    fun `multiple windows can be selected independently`() {
        val incoming = MutableSharedFlow<ControlMessage>()
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = WindowPickerViewModel(
            initialWindows = listOf(makeWindow(1u), makeWindow(2u), makeWindow(3u)),
            incomingMessages = incoming,
            scope = scope
        )

        viewModel.toggleSelection(1u)
        viewModel.toggleSelection(3u)

        val selection = viewModel.selection.value
        assertTrue(selection.contains(1u.toULong()))
        assertFalse(selection.contains(2u.toULong()))
        assertTrue(selection.contains(3u.toULong()))
    }

    @Test
    fun `selectedWindowIdsAsLongArray returns LongArray of selected ids`() {
        val incoming = MutableSharedFlow<ControlMessage>()
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = WindowPickerViewModel(
            initialWindows = listOf(makeWindow(10u), makeWindow(20u)),
            incomingMessages = incoming,
            scope = scope
        )

        viewModel.toggleSelection(10u)
        viewModel.toggleSelection(20u)

        val result = viewModel.selectedWindowIdsAsLongArray()
        assertEquals(2, result.size)
        assertTrue(result.contains(10L))
        assertTrue(result.contains(20L))
    }

    @Test
    fun `selectedWindowIdsAsLongArray returns empty array when nothing selected`() {
        val incoming = MutableSharedFlow<ControlMessage>()
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = WindowPickerViewModel(
            initialWindows = listOf(makeWindow(1u)),
            incomingMessages = incoming,
            scope = scope
        )

        assertArrayEquals(LongArray(0), viewModel.selectedWindowIdsAsLongArray())
    }

    @Test
    fun `non-catalogue messages are ignored without crashing`() = runBlocking {
        val incoming = MutableSharedFlow<ControlMessage>(replay = 8)
        val scope = CoroutineScope(Dispatchers.Default)
        val viewModel = buildViewModel(
            initialWindows = listOf(makeWindow(1u)),
            incoming = incoming,
            scope = scope
        )

        // Emit various unrelated message types — should not throw or alter catalogue.
        incoming.emit(ControlMessage.Heartbeat)
        incoming.emit(ControlMessage.StreamStopped(streamId = 1, reason = StreamStoppedReason.ClosedByViewer))
        incoming.emit(ControlMessage.ErrorMessage("ERR", "some error"))

        delay(100)
        assertEquals(1, viewModel.windowCatalogue.value.size)
        assertTrue(viewModel.windowCatalogue.value.containsKey(1u.toULong()))
    }
}
