package com.mtschoen.windowstream.viewer.app.ui

import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.WindowDescriptor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.launch

/**
 * Holds the mutable window catalogue for [WindowPickerScreen].
 *
 * Initialised from the [initialWindows] snapshot received in SERVER_HELLO, then
 * kept live by consuming WINDOW_ADDED / WINDOW_REMOVED / WINDOW_UPDATED messages
 * from [incomingMessages] while the picker is open.
 *
 * Selection state is stored as a set of windowIds; multi-select is allowed so
 * the user can queue up several windows before tapping Connect.
 */
class WindowPickerViewModel(
    initialWindows: List<WindowDescriptor>,
    incomingMessages: Flow<ControlMessage>,
    scope: CoroutineScope
) {
    private val windowCatalogueState: MutableStateFlow<Map<ULong, WindowDescriptor>> =
        MutableStateFlow(initialWindows.associateBy { it.windowId })

    private val selectionState: MutableStateFlow<Set<ULong>> = MutableStateFlow(emptySet())

    /** Current window catalogue keyed by windowId. Updated by live push events. */
    val windowCatalogue: StateFlow<Map<ULong, WindowDescriptor>> =
        windowCatalogueState.asStateFlow()

    /** Set of windowIds currently selected by the user. */
    val selection: StateFlow<Set<ULong>> = selectionState.asStateFlow()

    init {
        scope.launch {
            incomingMessages.filterIsInstance<ControlMessage.WindowAdded>().collect { event ->
                windowCatalogueState.value = windowCatalogueState.value +
                    (event.window.windowId to event.window)
            }
        }
        scope.launch {
            incomingMessages.filterIsInstance<ControlMessage.WindowRemoved>().collect { event ->
                windowCatalogueState.value = windowCatalogueState.value - event.windowId
                // Drop from selection if the window disappeared while the picker was open.
                selectionState.value = selectionState.value - event.windowId
            }
        }
        scope.launch {
            incomingMessages.filterIsInstance<ControlMessage.WindowUpdated>().collect { event ->
                val existing: WindowDescriptor = windowCatalogueState.value[event.windowId] ?: return@collect
                val updated: WindowDescriptor = existing.copy(
                    title = event.title ?: existing.title,
                    physicalWidth = event.physicalWidth ?: existing.physicalWidth,
                    physicalHeight = event.physicalHeight ?: existing.physicalHeight
                )
                windowCatalogueState.value = windowCatalogueState.value +
                    (event.windowId to updated)
            }
        }
    }

    /** Toggles [windowId] in the selection set. */
    fun toggleSelection(windowId: ULong) {
        val current: Set<ULong> = selectionState.value
        selectionState.value = if (windowId in current) {
            current - windowId
        } else {
            current + windowId
        }
    }

    /** Returns the currently selected windowIds as a [LongArray] for use in Intent extras. */
    fun selectedWindowIdsAsLongArray(): LongArray =
        selectionState.value.map { it.toLong() }.toLongArray()
}
