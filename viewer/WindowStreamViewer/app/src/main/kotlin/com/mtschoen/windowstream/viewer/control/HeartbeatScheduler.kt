package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.delay
import kotlin.time.Duration

class HeartbeatScheduler(
    private val sendInterval: Duration,
    private val silenceTimeout: Duration,
    private val currentTimeMilliseconds: () -> Long
) {
    @Volatile
    private var lastIncomingActivityMilliseconds: Long = 0L

    fun recordIncomingActivity() {
        lastIncomingActivityMilliseconds = currentTimeMilliseconds()
    }

    suspend fun run(
        sendHeartbeat: suspend () -> Unit,
        onTimeout: suspend () -> Unit
    ) {
        lastIncomingActivityMilliseconds = currentTimeMilliseconds()
        var lastSendTime: Long = currentTimeMilliseconds()
        while (true) {
            delay(200)
            val now: Long = currentTimeMilliseconds()
            val sinceIncoming: Long = now - lastIncomingActivityMilliseconds
            if (sinceIncoming >= silenceTimeout.inWholeMilliseconds) {
                onTimeout()
                return
            }
            val sinceSend: Long = now - lastSendTime
            if (sinceSend >= sendInterval.inWholeMilliseconds) {
                sendHeartbeat()
                lastSendTime = currentTimeMilliseconds()
            }
        }
    }
}
