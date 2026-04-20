package com.mtschoen.windowstream.viewer.decoder

import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlin.coroutines.coroutineContext
import kotlin.time.Duration

class StallMonitor(
    private val stallThreshold: Duration,
    private val currentTimeMilliseconds: () -> Long
) {
    @Volatile
    private var lastFrameMilliseconds: Long = currentTimeMilliseconds()
    @Volatile
    private var lastTriggerMilliseconds: Long = 0L

    fun recordFrameRendered() {
        lastFrameMilliseconds = currentTimeMilliseconds()
    }

    suspend fun run(onStalled: suspend () -> Unit) {
        lastFrameMilliseconds = currentTimeMilliseconds()
        while (coroutineContext.isActive) {
            val now: Long = currentTimeMilliseconds()
            val silence: Long = now - lastFrameMilliseconds
            if (silence >= stallThreshold.inWholeMilliseconds &&
                now - lastTriggerMilliseconds >= stallThreshold.inWholeMilliseconds) {
                onStalled()
                lastTriggerMilliseconds = now
            }
            delay(200)
        }
    }
}
