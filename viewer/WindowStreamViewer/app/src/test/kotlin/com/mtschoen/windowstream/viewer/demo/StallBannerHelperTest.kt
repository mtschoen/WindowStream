package com.mtschoen.windowstream.viewer.demo

import com.mtschoen.windowstream.viewer.control.StallCause
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class StallBannerHelperTest {

    @Test
    fun stallCauseSuffix_neverStarted() {
        assertEquals(" (never started)", stallCauseSuffix(StallCause.NeverStarted))
    }

    @Test
    fun stallCauseSuffix_sourceStalled() {
        assertEquals(" (stalled)", stallCauseSuffix(StallCause.SourceStalled))
    }

    @Test
    fun stallCauseSuffix_workerSilent() {
        assertEquals(" (worker silent)", stallCauseSuffix(StallCause.WorkerSilent))
    }

    @Test
    fun stallCauseSuffix_null() {
        assertEquals("", stallCauseSuffix(null))
    }
}
