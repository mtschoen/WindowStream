package com.mtschoen.windowstream.viewer.xr

import com.mtschoen.windowstream.viewer.control.StallCause
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Holds the list of open window panels for the spatial window manager.
 *
 * This is pure UI state: the Activity owns each panel's runtime resources
 * (decoder, [XrPanelSink], UDP receiver, pipeline scope) and the Compose-for-XR
 * scene renders from [panels]. Keeping the catalogue here (rather than in the
 * Activity) makes the spawn/close/minimize/resize logic unit-testable, the same
 * split used for the viewer's window picker flow.
 */
class SpatialWindowManager {
    private val panelsState: MutableStateFlow<List<SpatialPanelState>> =
        MutableStateFlow(emptyList())

    /** Open panels in spawn order. */
    val panels: StateFlow<List<SpatialPanelState>> = panelsState.asStateFlow()

    private val openWindowIdsState: MutableStateFlow<Set<ULong>> =
        MutableStateFlow(emptySet())

    /** Set of windowIds with an open panel; mirrors [panels] for fast lookup. */
    val openWindowIds: StateFlow<Set<ULong>> = openWindowIdsState.asStateFlow()

    /** True if a panel for [windowId] is currently open. */
    fun isOpen(windowId: ULong): Boolean =
        panelsState.value.any { it.windowId == windowId }

    /**
     * Adds a panel for a newly opened stream. No-op if [windowId] already has a
     * panel (the one-panel-per-window invariant; matches the server's
     * one-stream-per-window behaviour).
     */
    fun addPanel(
        windowId: ULong,
        streamId: Int,
        title: String,
        contentWidthPixels: Int,
        contentHeightPixels: Int,
    ) {
        if (isOpen(windowId)) return
        panelsState.value = panelsState.value + SpatialPanelState(
            windowId = windowId,
            streamId = streamId,
            title = title,
            contentWidthPixels = contentWidthPixels,
            contentHeightPixels = contentHeightPixels,
        )
        recomputeOpenIds()
    }

    /** Removes the panel for [windowId], if present. */
    fun removePanel(windowId: ULong) {
        val before: List<SpatialPanelState> = panelsState.value
        val after: List<SpatialPanelState> = before.filterNot { it.windowId == windowId }
        if (after.size != before.size) {
            panelsState.value = after
            recomputeOpenIds()
        }
    }

    /**
     * Toggles the minimized flag for [windowId]. Returns the new minimized
     * value, or null if no panel matches.
     */
    fun toggleMinimize(windowId: ULong): Boolean? {
        var newValue: Boolean? = null
        panelsState.value = panelsState.value.map { panel ->
            if (panel.windowId == windowId) {
                val toggled: SpatialPanelState = panel.copy(minimized = !panel.minimized)
                newValue = toggled.minimized
                toggled
            } else {
                panel
            }
        }
        return newValue
    }

    /** Adjusts the scale for [windowId] by [delta], clamped to the valid range. */
    fun adjustScale(windowId: ULong, delta: Float) {
        panelsState.value = panelsState.value.map { panel ->
            if (panel.windowId == windowId) {
                panel.copy(scale = SpatialPanelLayout.clampScale(panel.scale + delta))
            } else {
                panel
            }
        }
    }

    /**
     * Marks the panel for [streamId] as stalled or resumed.
     *
     * Stall/resume events arrive keyed by streamId (from the server's
     * STREAM_STALLED/STREAM_RESUMED messages), not by windowId, so this
     * mutator matches on [SpatialPanelState.streamId] unlike the windowId-keyed
     * mutators above. When [stalled] is false the [cause] is cleared to null.
     * No-op if no panel carries [streamId].
     */
    fun setStalled(streamId: Int, stalled: Boolean, cause: StallCause?) {
        panelsState.value = panelsState.value.map { panel ->
            if (panel.streamId == streamId) {
                panel.copy(
                    isStalled = stalled,
                    stallCause = if (stalled) cause else null,
                )
            } else {
                panel
            }
        }
    }

    /**
     * Updates the decoded content dimensions for [windowId] (e.g. on a
     * WINDOW_UPDATED resize). The panel aspect ratio follows from these.
     */
    fun updateDimensions(windowId: ULong, contentWidthPixels: Int, contentHeightPixels: Int) {
        panelsState.value = panelsState.value.map { panel ->
            if (panel.windowId == windowId) {
                panel.copy(
                    contentWidthPixels = contentWidthPixels,
                    contentHeightPixels = contentHeightPixels,
                )
            } else {
                panel
            }
        }
    }

    private fun recomputeOpenIds() {
        openWindowIdsState.value = panelsState.value.mapTo(mutableSetOf()) { it.windowId }
    }
}
