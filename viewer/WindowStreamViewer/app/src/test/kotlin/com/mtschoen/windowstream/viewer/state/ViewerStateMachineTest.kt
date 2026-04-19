package com.mtschoen.windowstream.viewer.state

import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.net.InetAddress

class ViewerStateMachineTest {
    private val machine = ViewerStateMachine()
    private val sampleServer = ServerInformation(
        hostname = "desk",
        host = InetAddress.getLoopbackAddress(),
        controlPort = 51000,
        protocolMajorVersion = 1,
        protocolMinorRevision = 1
    )

    @Test
    fun `starts in disconnected`() {
        assertTrue(machine.currentState is ViewerState.Disconnected)
    }

    @Test
    fun `disconnected to discovering on startDiscovery`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        assertTrue(machine.currentState is ViewerState.Discovering)
    }

    @Test
    fun `discovering to connected on serverSelected`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        machine.reduce(ViewerEvent.ServerSelected(sampleServer))
        val state: ViewerState = machine.currentState
        assertTrue(state is ViewerState.Connected)
        assertEquals(sampleServer, (state as ViewerState.Connected).server)
    }

    @Test
    fun `connected to streaming on streamStarted`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        machine.reduce(ViewerEvent.ServerSelected(sampleServer))
        machine.reduce(ViewerEvent.StreamStarted(streamId = 1, width = 1920, height = 1080))
        val state: ViewerState = machine.currentState
        assertTrue(state is ViewerState.Streaming)
    }

    @Test
    fun `streaming to error on fatal error`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        machine.reduce(ViewerEvent.ServerSelected(sampleServer))
        machine.reduce(ViewerEvent.StreamStarted(1, 1920, 1080))
        machine.reduce(ViewerEvent.FatalError("VIEWER_BUSY", "second viewer"))
        val state: ViewerState = machine.currentState
        assertTrue(state is ViewerState.Error)
        assertEquals("VIEWER_BUSY", (state as ViewerState.Error).code)
    }

    @Test
    fun `streaming to connected on streamStopped`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        machine.reduce(ViewerEvent.ServerSelected(sampleServer))
        machine.reduce(ViewerEvent.StreamStarted(1, 1920, 1080))
        machine.reduce(ViewerEvent.StreamStopped(1))
        assertTrue(machine.currentState is ViewerState.Connected)
    }

    @Test
    fun `any state to disconnected on disconnect`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        machine.reduce(ViewerEvent.ServerSelected(sampleServer))
        machine.reduce(ViewerEvent.Disconnect)
        assertTrue(machine.currentState is ViewerState.Disconnected)
    }

    @Test
    fun `invalid transition startDiscovery while already discovering is ignored`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        val stateAfterFirstDiscovery: ViewerState = machine.currentState
        machine.reduce(ViewerEvent.StartDiscovery)
        assertEquals(stateAfterFirstDiscovery, machine.currentState)
    }

    @Test
    fun `invalid transition serverSelected while not discovering is ignored`() {
        val disconnectedState: ViewerState = machine.currentState
        machine.reduce(ViewerEvent.ServerSelected(sampleServer))
        assertEquals(disconnectedState, machine.currentState)
    }

    @Test
    fun `invalid transition streamStopped while not streaming is ignored`() {
        machine.reduce(ViewerEvent.StartDiscovery)
        machine.reduce(ViewerEvent.ServerSelected(sampleServer))
        val connectedState: ViewerState = machine.currentState
        machine.reduce(ViewerEvent.StreamStopped(1))
        assertEquals(connectedState, machine.currentState)
    }

    @Test
    fun `invalid transition streamStarted while disconnected is ignored`() {
        val previousState: ViewerState = machine.currentState
        machine.reduce(ViewerEvent.StreamStarted(1, 1920, 1080))
        assertEquals(previousState, machine.currentState)
    }
}
