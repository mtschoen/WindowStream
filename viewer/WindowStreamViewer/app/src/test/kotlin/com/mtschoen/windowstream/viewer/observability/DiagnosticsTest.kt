package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class DiagnosticsTest {

    @Test
    fun `report on INFO event clears ThreadLocals on completion`() {
        Diagnostics.report(PipelineEvent.DiscoveryStarted)
        assertTrue(Diagnostics.currentPayload.get()!!.isEmpty())
        assertNull(Diagnostics.currentEvent.get())
    }

    @Test
    fun `report on WARNING event completes`() {
        Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
    }

    @Test
    fun `report on ERROR event with TcpConnectFailed completes`() {
        Diagnostics.report(
            PipelineEvent.TcpConnectFailed(host = "h", port = 1, cause = RuntimeException("boom"))
        )
    }

    @Test
    fun `report on ERROR event with DecoderFailed completes`() {
        Diagnostics.report(PipelineEvent.DecoderFailed(sid = 1, cause = RuntimeException("media")))
    }

    @Test
    fun `throwableOf returns cause for TcpConnectFailed`() {
        val cause = RuntimeException("refused")
        assertSame(cause, Diagnostics.throwableOf(PipelineEvent.TcpConnectFailed("h", 1, cause)))
    }

    @Test
    fun `throwableOf returns cause for DecoderFailed`() {
        val cause = RuntimeException("codec")
        assertSame(cause, Diagnostics.throwableOf(PipelineEvent.DecoderFailed(1, cause)))
    }

    @Test
    fun `throwableOf returns null for events without throwables`() {
        assertNull(Diagnostics.throwableOf(PipelineEvent.DiscoveryStarted))
    }

    @Test
    fun `payloadOf branches covered via report calls`() {
        Diagnostics.report(PipelineEvent.DiscoveryResultReceived("h", "1.2.3.4", 9))
        Diagnostics.report(PipelineEvent.TcpConnecting("h", 9))
        Diagnostics.report(PipelineEvent.TcpConnected(42L))
        Diagnostics.report(PipelineEvent.ServerHelloReceived(1, 2))
        Diagnostics.report(PipelineEvent.OpenStreamSent(7UL))
        Diagnostics.report(PipelineEvent.StreamOpened(sid = 1, width = 1920, height = 1080))
        Diagnostics.report(PipelineEvent.StreamRefused(sid = 2, errorCode = "E", message = "m"))
        Diagnostics.report(PipelineEvent.StreamStopped(sid = 3, reason = "r"))
        Diagnostics.report(PipelineEvent.UdpBound(port = 49152))
        Diagnostics.report(PipelineEvent.UdpFirstPacketReceived(sid = 4, delayMs = 13L))
        Diagnostics.report(PipelineEvent.UdpStalled(sid = 5, gapMs = 2000L))
        Diagnostics.report(PipelineEvent.DecoderStarting(sid = 6, width = 1280, height = 720))
        Diagnostics.report(PipelineEvent.DecoderStarted(sid = 7))
        Diagnostics.report(PipelineEvent.SurfaceCreated(panelIndex = 0))
        Diagnostics.report(PipelineEvent.SurfaceDestroyed(panelIndex = 1, reasonHint = "lifecycle"))
        Diagnostics.report(PipelineEvent.FramesPresenting(sid = 8, fps = 60.0))
        Diagnostics.report(PipelineEvent.WifiLockAcquired)
        Diagnostics.report(PipelineEvent.WifiLockReleased)
    }
}
