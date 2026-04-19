package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.currentTime
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

@OptIn(ExperimentalCoroutinesApi::class)
class HeartbeatSchedulerTest {

    @Test
    fun `sends heartbeat every two seconds`() = runTest {
        val testScope: TestScope = this
        val sent: MutableList<Long> = mutableListOf()
        val scheduler = HeartbeatScheduler(
            sendInterval = 2.seconds,
            silenceTimeout = 6.seconds,
            currentTimeMilliseconds = { testScope.currentTime }
        )
        val job = testScope.launch {
            scheduler.run(
                sendHeartbeat = { sent.add(testScope.currentTime) },
                onTimeout = { assertTrue(false, "timeout not expected") }
            )
        }
        scheduler.recordIncomingActivity()
        advanceTimeBy(2_100.milliseconds)
        scheduler.recordIncomingActivity()
        advanceTimeBy(2_100.milliseconds)
        scheduler.recordIncomingActivity()
        advanceTimeBy(2_100.milliseconds)
        job.cancel()
        assertEquals(3, sent.size)
    }

    @Test
    fun `fires timeout after six seconds of silence`() = runTest {
        val testScope: TestScope = this
        var timedOut = false
        val scheduler = HeartbeatScheduler(
            sendInterval = 2.seconds,
            silenceTimeout = 6.seconds,
            currentTimeMilliseconds = { testScope.currentTime }
        )
        val job = launch {
            scheduler.run(
                sendHeartbeat = { },
                onTimeout = { timedOut = true }
            )
        }
        scheduler.recordIncomingActivity()
        advanceTimeBy(6_100.milliseconds)
        job.join()
        assertTrue(timedOut)
    }

    @Test
    fun `resets silence timer when activity recorded`() = runTest {
        val testScope: TestScope = this
        var timedOut = false
        val scheduler = HeartbeatScheduler(
            sendInterval = 2.seconds,
            silenceTimeout = 6.seconds,
            currentTimeMilliseconds = { testScope.currentTime }
        )
        val job = launch {
            scheduler.run(
                sendHeartbeat = { },
                onTimeout = { timedOut = true }
            )
        }
        scheduler.recordIncomingActivity()
        advanceTimeBy(5_000.milliseconds)
        scheduler.recordIncomingActivity()
        advanceTimeBy(5_000.milliseconds)
        assertFalse(timedOut)
        job.cancel()
    }
}
