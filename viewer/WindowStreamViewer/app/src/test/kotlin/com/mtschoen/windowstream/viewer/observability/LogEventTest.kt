package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Test
import java.time.Instant

class LogEventTest {

    @Test
    fun `carries all fields and defaults pipelineEvent to null`() {
        val timestamp = Instant.parse("2026-05-17T00:00:00Z")
        val payload = mapOf<String, Any?>("k" to 1)
        val event = LogEvent(
            timestamp = timestamp,
            severity = Severity.INFO,
            eventType = "DiscoveryStarted",
            streamId = 7,
            message = "hello",
            payload = payload,
            throwable = null,
        )
        assertSame(timestamp, event.timestamp)
        assertEquals(Severity.INFO, event.severity)
        assertEquals("DiscoveryStarted", event.eventType)
        assertEquals(7, event.streamId)
        assertEquals("hello", event.message)
        assertSame(payload, event.payload)
        assertNull(event.throwable)
        assertNull(event.pipelineEvent)
    }

    @Test
    fun `carries pipelineEvent and throwable when provided`() {
        val cause = RuntimeException("boom")
        val pipeline = PipelineEvent.TcpConnectFailed(host = "h", port = 1, cause = cause)
        val event = LogEvent(
            timestamp = Instant.EPOCH,
            severity = Severity.ERROR,
            eventType = "TcpConnectFailed",
            streamId = null,
            message = "failed",
            payload = emptyMap(),
            throwable = cause,
            pipelineEvent = pipeline,
        )
        assertSame(cause, event.throwable)
        assertSame(pipeline, event.pipelineEvent)
    }
}
