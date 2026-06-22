package com.mtschoen.windowstream.viewer.demo

import com.mtschoen.windowstream.viewer.control.StallCause

/**
 * Pure helper for deriving the stall banner suffix from a [StallCause].
 * Lives outside [UnifiedStreamingActivity] so it can be unit-tested without
 * instantiating an Android Activity.
 *
 * The null-early-return form avoids the synthetic JaCoCo `else` branch that
 * an exhaustive `when (nullable)` would generate, keeping the coverage gate green
 * without a class-level exclusion.
 */
fun stallCauseSuffix(cause: StallCause?): String {
    if (cause == null) return ""
    return when (cause) {
        StallCause.NeverStarted -> " (never started)"
        StallCause.SourceStalled -> " (stalled)"
        StallCause.WorkerSilent -> " (worker silent)"
    }
}
