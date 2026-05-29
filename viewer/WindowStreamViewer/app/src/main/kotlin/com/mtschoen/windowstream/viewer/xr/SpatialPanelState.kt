package com.mtschoen.windowstream.viewer.xr

/**
 * Immutable UI state for one open window panel in the spatial window manager.
 *
 * The pixel dimensions are the decoded stream size and drive the panel aspect
 * ratio (see [computePanelDimensionsMeters]); [scale] multiplies the rendered
 * panel size so the user can grow/shrink a panel, and [minimized] collapses it
 * to a chrome-only chip (with the underlying stream paused).
 */
data class SpatialPanelState(
    val windowId: ULong,
    val streamId: Int,
    val title: String,
    val contentWidthPixels: Int,
    val contentHeightPixels: Int,
    val minimized: Boolean = false,
    val scale: Float = 1.0f,
)
