package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Test

class PipelineEventTest {

    @Test
    fun `DiscoveryStarted is INFO and stream-less`() {
        assertEquals(Severity.INFO, PipelineEvent.DiscoveryStarted.severity)
        assertNull(PipelineEvent.DiscoveryStarted.streamId)
    }

    @Test
    fun `DiscoveryTimedOut is WARNING and stream-less`() {
        assertEquals(Severity.WARNING, PipelineEvent.DiscoveryTimedOut.severity)
        assertNull(PipelineEvent.DiscoveryTimedOut.streamId)
    }

    @Test
    fun `WifiLockAcquired is INFO and stream-less`() {
        assertEquals(Severity.INFO, PipelineEvent.WifiLockAcquired.severity)
        assertNull(PipelineEvent.WifiLockAcquired.streamId)
    }

    @Test
    fun `WifiLockReleased is INFO and stream-less`() {
        assertEquals(Severity.INFO, PipelineEvent.WifiLockReleased.severity)
        assertNull(PipelineEvent.WifiLockReleased.streamId)
    }

    @Test
    fun `DiscoveryResultReceived carries hostname address port`() {
        val event = PipelineEvent.DiscoveryResultReceived(
            hostname = "chonkers",
            address = "192.168.1.10",
            port = 53234,
        )
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals("chonkers", event.hostname)
        assertEquals("192.168.1.10", event.address)
        assertEquals(53234, event.port)
    }

    @Test
    fun `TcpConnecting carries host and port`() {
        val event = PipelineEvent.TcpConnecting(host = "1.2.3.4", port = 53234)
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals("1.2.3.4", event.host)
        assertEquals(53234, event.port)
    }

    @Test
    fun `TcpConnected carries durationMs`() {
        val event = PipelineEvent.TcpConnected(durationMs = 42L)
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals(42L, event.durationMs)
    }

    @Test
    fun `TcpConnectFailed is ERROR and carries host, port, cause`() {
        val cause = RuntimeException("refused")
        val event = PipelineEvent.TcpConnectFailed(host = "1.2.3.4", port = 53234, cause = cause)
        assertEquals(Severity.ERROR, event.severity)
        assertNull(event.streamId)
        assertEquals("1.2.3.4", event.host)
        assertEquals(53234, event.port)
        assertSame(cause, event.cause)
    }

    @Test
    fun `ServerHelloReceived carries windowCount and udpPort`() {
        val event = PipelineEvent.ServerHelloReceived(windowCount = 3, udpPort = 53235)
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals(3, event.windowCount)
        assertEquals(53235, event.udpPort)
    }

    @Test
    fun `OpenStreamSent carries windowId`() {
        val event = PipelineEvent.OpenStreamSent(windowId = 7UL)
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals(7UL, event.windowId)
    }

    @Test
    fun `StreamOpened carries sid width height and exposes streamId on base`() {
        val event = PipelineEvent.StreamOpened(sid = 1, width = 1920, height = 1080)
        assertEquals(Severity.INFO, event.severity)
        assertEquals(1, event.streamId)
        assertEquals(1, event.sid)
        assertEquals(1920, event.width)
        assertEquals(1080, event.height)
    }

    @Test
    fun `StreamRefused is WARNING and carries sid errorCode message`() {
        val event = PipelineEvent.StreamRefused(sid = 2, errorCode = "WGC_FAIL", message = "bad")
        assertEquals(Severity.WARNING, event.severity)
        assertEquals(2, event.streamId)
        assertEquals(2, event.sid)
        assertEquals("WGC_FAIL", event.errorCode)
        assertEquals("bad", event.message)
    }

    @Test
    fun `StreamStopped carries sid and reason`() {
        val event = PipelineEvent.StreamStopped(sid = 3, reason = "viewer-disconnect")
        assertEquals(Severity.INFO, event.severity)
        assertEquals(3, event.streamId)
        assertEquals(3, event.sid)
        assertEquals("viewer-disconnect", event.reason)
    }

    @Test
    fun `UdpBound carries port`() {
        val event = PipelineEvent.UdpBound(port = 49152)
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals(49152, event.port)
    }

    @Test
    fun `UdpFirstPacketReceived carries sid and delayMs`() {
        val event = PipelineEvent.UdpFirstPacketReceived(sid = 4, delayMs = 13L)
        assertEquals(Severity.INFO, event.severity)
        assertEquals(4, event.streamId)
        assertEquals(4, event.sid)
        assertEquals(13L, event.delayMs)
    }

    @Test
    fun `UdpStalled is WARNING and carries sid and gapMs`() {
        val event = PipelineEvent.UdpStalled(sid = 5, gapMs = 2000L)
        assertEquals(Severity.WARNING, event.severity)
        assertEquals(5, event.streamId)
        assertEquals(5, event.sid)
        assertEquals(2000L, event.gapMs)
    }

    @Test
    fun `DecoderStarting carries sid width height`() {
        val event = PipelineEvent.DecoderStarting(sid = 6, width = 1280, height = 720)
        assertEquals(Severity.INFO, event.severity)
        assertEquals(6, event.streamId)
        assertEquals(6, event.sid)
        assertEquals(1280, event.width)
        assertEquals(720, event.height)
    }

    @Test
    fun `DecoderStarted carries sid`() {
        val event = PipelineEvent.DecoderStarted(sid = 7)
        assertEquals(Severity.INFO, event.severity)
        assertEquals(7, event.streamId)
        assertEquals(7, event.sid)
    }

    @Test
    fun `DecoderFailed is ERROR and carries sid cause`() {
        val cause = RuntimeException("media-codec")
        val event = PipelineEvent.DecoderFailed(sid = 8, cause = cause)
        assertEquals(Severity.ERROR, event.severity)
        assertEquals(8, event.streamId)
        assertEquals(8, event.sid)
        assertSame(cause, event.cause)
    }

    @Test
    fun `SurfaceCreated carries panelIndex`() {
        val event = PipelineEvent.SurfaceCreated(panelIndex = 0)
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals(0, event.panelIndex)
    }

    @Test
    fun `SurfaceDestroyed carries panelIndex and reasonHint`() {
        val event = PipelineEvent.SurfaceDestroyed(panelIndex = 1, reasonHint = "lifecycle")
        assertEquals(Severity.INFO, event.severity)
        assertNull(event.streamId)
        assertEquals(1, event.panelIndex)
        assertEquals("lifecycle", event.reasonHint)
    }

    @Test
    fun `FramesPresenting carries sid and fps`() {
        val event = PipelineEvent.FramesPresenting(sid = 9, fps = 59.94)
        assertEquals(Severity.INFO, event.severity)
        assertEquals(9, event.streamId)
        assertEquals(9, event.sid)
        assertEquals(59.94, event.fps)
    }
}
