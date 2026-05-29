package com.mtschoen.windowstream.viewer.xr

/**
 * Pure placement and scale math for the spatial window manager. Kept out of
 * the Compose layer so it is unit-testable on the JVM (the Compose-for-XR
 * scene is excluded from coverage like the rest of the XR rendering).
 */
object SpatialPanelLayout {
    /** Smallest user-selectable panel scale multiplier. */
    const val MIN_SCALE: Float = 0.5f

    /** Largest user-selectable panel scale multiplier. */
    const val MAX_SCALE: Float = 3.0f

    /** Increment applied by the grow/shrink chrome buttons. */
    const val SCALE_STEP: Float = 0.25f

    /** Horizontal spacing between adjacent panel slots, in meters. */
    const val SLOT_SPACING_METERS: Float = 1.4f

    /** Clamps [value] into the `[MIN_SCALE, MAX_SCALE]` range. */
    fun clampScale(value: Float): Float = value.coerceIn(MIN_SCALE, MAX_SCALE)

    /**
     * Horizontal offset in meters for panel [index] of [count] total, fanning
     * panels symmetrically around x = 0 so newly spawned panels do not overlap.
     *
     * Examples: index 0 of 1 -> 0; index 0 of 2 -> -0.7; index 1 of 2 -> +0.7.
     */
    fun computeSlotOffsetMeters(index: Int, count: Int): Float {
        require(count > 0) { "count must be positive, was $count" }
        require(index in 0 until count) { "index $index out of range for count $count" }
        val centeredIndex: Float = index - (count - 1) / 2.0f
        return centeredIndex * SLOT_SPACING_METERS
    }
}
