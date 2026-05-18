package com.mtschoen.windowstream.viewer.observability

import java.time.Instant

data class LogEvent(
    val timestamp: Instant,
    val severity: Severity,
    val eventType: String,
    val streamId: Int?,
    val message: String,
    val payload: Map<String, Any?>,
    val throwable: Throwable?,
    val pipelineEvent: PipelineEvent? = null,
)
