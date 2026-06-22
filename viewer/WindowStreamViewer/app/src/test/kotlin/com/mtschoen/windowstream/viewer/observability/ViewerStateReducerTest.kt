package com.mtschoen.windowstream.viewer.observability

import com.mtschoen.windowstream.viewer.control.StallCause
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class ViewerStateReducerTest {

    @Test
    fun `initial state is all Pending`() {
        val reducer = ViewerStateReducer()
        assertEquals(StageStatus.Pending, reducer.state.discovery)
        assertEquals(StageStatus.Pending, reducer.state.tcpConnect)
        assertEquals(StageStatus.Pending, reducer.state.serverHello)
        assertNull(reducer.state.discoveredServer)
        assertNull(reducer.state.tcpConnectError)
        assertEquals(0, reducer.state.windowCount)
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `DiscoveryStarted sets discovery InProgress`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.DiscoveryStarted)
        assertEquals(StageStatus.InProgress, reducer.state.discovery)
    }

    @Test
    fun `DiscoveryResultReceived sets discovery Ok with formatted server`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.DiscoveryResultReceived("chonkers", "192.168.1.10", 53234))
        assertEquals(StageStatus.Ok, reducer.state.discovery)
        assertEquals("chonkers (192.168.1.10:53234)", reducer.state.discoveredServer)
    }

    @Test
    fun `DiscoveryTimedOut sets discovery Warning`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.DiscoveryTimedOut)
        assertEquals(StageStatus.Warning, reducer.state.discovery)
    }

    @Test
    fun `TcpConnecting sets tcpConnect InProgress`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.TcpConnecting("host", 5555))
        assertEquals(StageStatus.InProgress, reducer.state.tcpConnect)
    }

    @Test
    fun `TcpConnected sets tcpConnect Ok`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.TcpConnected(durationMs = 42))
        assertEquals(StageStatus.Ok, reducer.state.tcpConnect)
    }

    @Test
    fun `TcpConnectFailed sets tcpConnect Error with message`() {
        val reducer = ViewerStateReducer()
        val cause = RuntimeException("socket refused")
        reducer.apply(PipelineEvent.TcpConnectFailed("host", 5555, cause))
        assertEquals(StageStatus.Error, reducer.state.tcpConnect)
        assertEquals("socket refused", reducer.state.tcpConnectError)
    }

    @Test
    fun `ServerHelloReceived sets serverHello Ok and stores windowCount`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.ServerHelloReceived(windowCount = 5, udpPort = 50000))
        assertEquals(StageStatus.Ok, reducer.state.serverHello)
        assertEquals(5, reducer.state.windowCount)
    }

    @Test
    fun `OpenStreamSent on empty stream map inserts placeholder InProgress row`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        assertEquals(StageStatus.InProgress, reducer.state.streams[-1]?.openStream)
    }

    @Test
    fun `OpenStreamSent twice in a row reuses existing placeholder row`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.OpenStreamSent(8UL))
        assertEquals(StageStatus.InProgress, reducer.state.streams[-1]?.openStream)
        assertEquals(1, reducer.state.streams.size)
    }

    @Test
    fun `StreamOpened clears placeholder and inserts sid row Ok`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 1920, height = 1080))
        assertNull(reducer.state.streams[-1])
        assertEquals(StageStatus.Ok, reducer.state.streams[1]?.openStream)
    }

    @Test
    fun `StreamRefused after StreamOpened uses existing sid row`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 1920, height = 1080))
        reducer.apply(PipelineEvent.StreamRefused(sid = 1, errorCode = "WGC_FAIL", message = "WGC E_FAIL"))
        assertEquals(StageStatus.Error, reducer.state.streams[1]?.openStream)
        assertEquals("WGC E_FAIL", reducer.state.streams[1]?.openStreamError)
    }

    @Test
    fun `StreamRefused after only OpenStreamSent uses placeholder row`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamRefused(sid = 1, errorCode = "WGC_FAIL", message = "WGC E_FAIL"))
        assertEquals(StageStatus.Error, reducer.state.streams[1]?.openStream)
        assertEquals("WGC E_FAIL", reducer.state.streams[1]?.openStreamError)
        assertNull(reducer.state.streams[-1])
    }

    @Test
    fun `StreamRefused cold with no prior open uses fresh StreamRowState`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.StreamRefused(sid = 9, errorCode = "X", message = "cold refusal"))
        assertEquals(StageStatus.Error, reducer.state.streams[9]?.openStream)
        assertEquals("cold refusal", reducer.state.streams[9]?.openStreamError)
    }

    @Test
    fun `StreamStopped removes sid row`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 100, height = 100))
        reducer.apply(PipelineEvent.StreamStopped(sid = 1, reason = "client"))
        assertNull(reducer.state.streams[1])
    }

    @Test
    fun `UdpFirstPacketReceived marks row Ok with delay`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 100, height = 100))
        reducer.apply(PipelineEvent.UdpFirstPacketReceived(sid = 1, delayMs = 15))
        assertEquals(StageStatus.Ok, reducer.state.streams[1]?.udpArriving)
        assertEquals(15L, reducer.state.streams[1]?.udpFirstDelayMs)
    }

    @Test
    fun `UdpFirstPacketReceived with no matching row is a no-op`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.UdpFirstPacketReceived(sid = 99, delayMs = 5))
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `UdpStalled marks row Warning`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 100, height = 100))
        reducer.apply(PipelineEvent.UdpStalled(sid = 1, gapMs = 2000))
        assertEquals(StageStatus.Warning, reducer.state.streams[1]?.udpArriving)
    }

    @Test
    fun `UdpStalled with no matching row is a no-op`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.UdpStalled(sid = 99, gapMs = 2000))
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `DecoderStarted marks row Ok`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 100, height = 100))
        reducer.apply(PipelineEvent.DecoderStarted(sid = 1))
        assertEquals(StageStatus.Ok, reducer.state.streams[1]?.decoder)
    }

    @Test
    fun `DecoderStarted with no matching row is a no-op`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.DecoderStarted(sid = 99))
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `DecoderFailed marks row Error with cause message`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 100, height = 100))
        reducer.apply(PipelineEvent.DecoderFailed(sid = 1, cause = RuntimeException("codec died")))
        assertEquals(StageStatus.Error, reducer.state.streams[1]?.decoder)
        assertEquals("codec died", reducer.state.streams[1]?.decoderError)
    }

    @Test
    fun `DecoderFailed with no matching row is a no-op`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.DecoderFailed(sid = 99, cause = RuntimeException("x")))
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `FramesPresenting marks row Ok with fps`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 100, height = 100))
        reducer.apply(PipelineEvent.FramesPresenting(sid = 1, fps = 60.0))
        assertEquals(StageStatus.Ok, reducer.state.streams[1]?.presenting)
        assertEquals(60.0, reducer.state.streams[1]?.fps)
    }

    @Test
    fun `FramesPresenting with no matching row is a no-op`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.FramesPresenting(sid = 99, fps = 30.0))
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `SourceStalled then SourceResumed toggles isStalled and stallCause`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.OpenStreamSent(7UL))
        reducer.apply(PipelineEvent.StreamOpened(sid = 1, width = 100, height = 100))

        reducer.apply(PipelineEvent.SourceStalled(sid = 1, cause = StallCause.SourceStalled))
        assertTrue(reducer.state.streams[1]?.isStalled == true)
        assertEquals(StallCause.SourceStalled, reducer.state.streams[1]?.stallCause)

        reducer.apply(PipelineEvent.SourceResumed(sid = 1))
        assertFalse(reducer.state.streams[1]?.isStalled == true)
        assertNull(reducer.state.streams[1]?.stallCause)
    }

    @Test
    fun `SourceStalled with no matching row is a no-op`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.SourceStalled(sid = 99, cause = StallCause.WorkerSilent))
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `SourceResumed with no matching row is a no-op`() {
        val reducer = ViewerStateReducer()
        reducer.apply(PipelineEvent.SourceResumed(sid = 99))
        assertTrue(reducer.state.streams.isEmpty())
    }

    @Test
    fun `unhandled events fall through to else arm leaving state unchanged`() {
        val reducer = ViewerStateReducer()
        val before = reducer.state
        reducer.apply(PipelineEvent.UdpBound(port = 50000))
        reducer.apply(PipelineEvent.DecoderStarting(sid = 1, width = 100, height = 100))
        reducer.apply(PipelineEvent.SurfaceCreated(panelIndex = 0))
        reducer.apply(PipelineEvent.SurfaceDestroyed(panelIndex = 0, reasonHint = "lifecycle"))
        reducer.apply(PipelineEvent.WifiLockAcquired)
        reducer.apply(PipelineEvent.WifiLockReleased)
        assertEquals(before, reducer.state)
    }

    @Test
    fun `StreamRowState defaults are Pending and null`() {
        val row = StreamRowState()
        assertEquals(StageStatus.Pending, row.openStream)
        assertNull(row.openStreamError)
        assertEquals(StageStatus.Pending, row.udpArriving)
        assertNull(row.udpFirstDelayMs)
        assertEquals(StageStatus.Pending, row.decoder)
        assertNull(row.decoderError)
        assertEquals(StageStatus.Pending, row.presenting)
        assertNull(row.fps)
    }

    @Test
    fun `ViewerState equality uses all fields`() {
        val a = ViewerState(discovery = StageStatus.Ok, windowCount = 1)
        val b = a.copy()
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
    }

    @Test
    fun `StageStatus enum covers all five states`() {
        val states = StageStatus.entries
        assertEquals(5, states.size)
        assertNotNull(StageStatus.valueOf("Pending"))
        assertNotNull(StageStatus.valueOf("InProgress"))
        assertNotNull(StageStatus.valueOf("Ok"))
        assertNotNull(StageStatus.valueOf("Warning"))
        assertNotNull(StageStatus.valueOf("Error"))
    }
}
