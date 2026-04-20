package com.mtschoen.windowstream.viewer.decoder

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

@OptIn(ExperimentalCoroutinesApi::class)
class StallMonitorTest {
    @Test
    fun `fires once after two seconds without frame`() = runTest {
        var triggerCount = 0
        val scheduler = testScheduler
        val monitor = StallMonitor(
            stallThreshold = 2.seconds,
            currentTimeMilliseconds = { scheduler.currentTime }
        )
        val job = launch { monitor.run { triggerCount++ } }
        monitor.recordFrameRendered()
        advanceTimeBy(2_100.milliseconds)
        assertEquals(1, triggerCount)
        job.cancel()
    }

    @Test
    fun `does not re-fire within threshold after first stall trigger`() = runTest {
        var triggerCount = 0
        val scheduler = testScheduler
        val monitor = StallMonitor(
            stallThreshold = 2.seconds,
            currentTimeMilliseconds = { scheduler.currentTime }
        )
        val job = launch { monitor.run { triggerCount++ } }
        monitor.recordFrameRendered()
        // Advance past stall threshold — fires once.
        advanceTimeBy(2_100.milliseconds)
        assertEquals(1, triggerCount)
        // Advance only 200 ms more — silence still > threshold but lastTriggerMilliseconds is recent,
        // so the second condition (now - lastTriggerMilliseconds >= threshold) evaluates to false.
        advanceTimeBy(200.milliseconds)
        assertEquals(1, triggerCount)
        job.cancel()
    }

    @Test
    fun `does not fire if frames arrive`() = runTest {
        var triggerCount = 0
        val scheduler = testScheduler
        val monitor = StallMonitor(
            stallThreshold = 2.seconds,
            currentTimeMilliseconds = { scheduler.currentTime }
        )
        val job = launch { monitor.run { triggerCount++ } }
        monitor.recordFrameRendered()
        advanceTimeBy(1_500.milliseconds)
        monitor.recordFrameRendered()
        advanceTimeBy(1_500.milliseconds)
        assertEquals(0, triggerCount)
        job.cancel()
    }
}
