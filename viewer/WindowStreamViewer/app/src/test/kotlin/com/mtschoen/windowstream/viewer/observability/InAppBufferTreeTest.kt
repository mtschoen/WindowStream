package com.mtschoen.windowstream.viewer.observability

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Test
import timber.log.Timber

class InAppBufferTreeTest {

    private inline fun withPlanted(tree: InAppBufferTree, block: () -> Unit) {
        Timber.plant(tree)
        try { block() } finally { Timber.uproot(tree) }
    }

    @Test
    fun `WARNING event from Diagnostics yields WARNING LogEvent`() = runBlocking {
        val tree = InAppBufferTree(replay = 16)
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
        }
        val received = tree.events.first()
        assertEquals("DiscoveryTimedOut", received.eventType)
        assertEquals(Severity.WARNING, received.severity)
        assertNull(received.streamId)
        assertNull(received.throwable)
        assertSame(PipelineEvent.DiscoveryTimedOut, received.pipelineEvent)
    }

    @Test
    fun `INFO event from Diagnostics yields INFO LogEvent`() = runBlocking {
        val tree = InAppBufferTree(replay = 16)
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.DiscoveryStarted)
        }
        val received = tree.events.first()
        assertEquals("DiscoveryStarted", received.eventType)
        assertEquals(Severity.INFO, received.severity)
    }

    @Test
    fun `ERROR event from Diagnostics yields ERROR LogEvent with throwable`() = runBlocking {
        val tree = InAppBufferTree(replay = 16)
        val cause = RuntimeException("refused")
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.TcpConnectFailed(host = "h", port = 1, cause = cause))
        }
        val received = tree.events.first()
        assertEquals("TcpConnectFailed", received.eventType)
        assertEquals(Severity.ERROR, received.severity)
        assertSame(cause, received.throwable)
    }

    @Test
    fun `streamId is captured from payload for stream-scoped events`() = runBlocking {
        val tree = InAppBufferTree(replay = 16)
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.StreamOpened(sid = 42, width = 1920, height = 1080))
        }
        val received = tree.events.first()
        assertEquals(42, received.streamId)
        assertEquals("StreamOpened", received.eventType)
    }

    @Test
    fun `raw Timber call without Diagnostics context defaults eventType to Log`() = runBlocking {
        val tree = InAppBufferTree(replay = 16)
        withPlanted(tree) {
            Timber.tag("Other").i("plain message")
        }
        val received = tree.events.first()
        assertEquals("Log", received.eventType)
        assertNull(received.streamId)
        assertNull(received.pipelineEvent)
        assertEquals("plain message", received.message)
    }

    @Test
    fun `default-replay constructor accepts events`() = runBlocking {
        val tree = InAppBufferTree()
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.WifiLockAcquired)
        }
        val received = tree.events.first()
        assertEquals("WifiLockAcquired", received.eventType)
        assertNotNull(received.timestamp)
    }
}
