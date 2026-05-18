package com.mtschoen.windowstream.viewer.observability

import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import timber.log.Timber
import java.time.Instant

class InAppBufferTree(replay: Int = 200) : Timber.Tree() {

    private val _events = MutableSharedFlow<LogEvent>(replay = replay, extraBufferCapacity = 64)
    val events: SharedFlow<LogEvent> = _events.asSharedFlow()

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        val payload = Diagnostics.currentPayload.get()
        val severity = when {
            priority >= android.util.Log.ERROR -> Severity.ERROR
            priority >= android.util.Log.WARN -> Severity.WARNING
            else -> Severity.INFO
        }
        val logEvent = LogEvent(
            timestamp = Instant.now(),
            severity = severity,
            eventType = (payload["eventType"] as? String) ?: "Log",
            streamId = payload["streamId"] as? Int,
            message = message,
            payload = payload,
            throwable = t,
            pipelineEvent = Diagnostics.currentEvent.get(),
        )
        _events.tryEmit(logEvent)
    }
}
