package com.mtschoen.windowstream.viewer.app

import com.mtschoen.windowstream.viewer.control.ActiveStreamDescriptor
import com.mtschoen.windowstream.viewer.control.ControlClient
import com.mtschoen.windowstream.viewer.control.ControlConnection
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.decoder.FrameSink
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.state.ViewerEvent
import com.mtschoen.windowstream.viewer.state.ViewerState
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import io.mockk.Runs
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.just
import io.mockk.mockk
import io.mockk.verify
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import java.net.InetAddress
import kotlin.time.Duration.Companion.seconds

class ViewerPipelineTest {

    private val fakeHost: InetAddress = InetAddress.getLoopbackAddress()

    private val sampleServer = ServerInformation(
        hostname = "desk",
        host = fakeHost,
        controlPort = 51000,
        protocolMajorVersion = 1,
        protocolMinorRevision = 1
    )

    private val sampleDescriptor = ActiveStreamDescriptor(
        streamId = 1,
        udpPort = 52000,
        codec = "h264",
        width = 1920,
        height = 1080,
        framesPerSecond = 60
    )

    private val frameSink: FrameSink = mockk(relaxed = true)
    private val controlClient: ControlClient = mockk()
    private val controlConnection: ControlConnection = mockk(relaxed = true)
    private val udpTransportReceiver: UdpTransportReceiver = mockk(relaxed = true)
    private val mediaCodecDecoder: MediaCodecDecoder = mockk(relaxed = true)

    // Test-controlled flow for injecting ControlMessages into the pipeline.
    private val incomingFlow = MutableSharedFlow<ControlMessage>(replay = 8, extraBufferCapacity = 32)

    // Captured keyframe-request lambda — set when beginStreaming runs.
    private var capturedKeyframeCallback: (suspend () -> Unit)? = null

    private lateinit var pipeline: ViewerPipeline
    private lateinit var testScope: CoroutineScope
    private lateinit var testScopeJob: Job

    @BeforeEach
    fun setUp() {
        every { controlConnection.incoming } returns incomingFlow.asSharedFlow()
        coEvery { controlConnection.send(any()) } just Runs
        coEvery { controlClient.connect(any()) } returns controlConnection

        val frameFlow: SharedFlow<EncodedFrame> = MutableSharedFlow<EncodedFrame>().asSharedFlow()
        every { udpTransportReceiver.start(any()) } returns frameFlow
        every { mediaCodecDecoder.start(any(), any(), any(), any()) } just Runs

        pipeline = ViewerPipeline(
            frameSink = frameSink,
            displayCapabilities = DisplayCapabilities(3840, 2160, listOf("h264")),
            controlClientFactory = { _, _, _ -> controlClient },
            udpTransportReceiverFactory = { _, _ -> udpTransportReceiver },
            mediaCodecDecoderFactory = { _, onKeyframeRequested ->
                capturedKeyframeCallback = onKeyframeRequested
                mediaCodecDecoder
            }
        )

        testScopeJob = Job()
        testScope = CoroutineScope(Dispatchers.IO + testScopeJob)
    }

    @AfterEach
    fun tearDown() = runBlocking {
        testScopeJob.cancelAndJoin()
    }

    // Convenience: advance state machine to Discovering then connect.
    private suspend fun connectPipeline() {
        pipeline.stateMachine.reduce(ViewerEvent.StartDiscovery)
        pipeline.connect(testScope, sampleServer)
    }

    // Wait until the state machine reaches the expected state, or time out.
    private suspend fun awaitState(predicate: (ViewerState) -> Boolean) {
        withTimeout(3.seconds) {
            while (!predicate(pipeline.stateMachine.currentState)) {
                kotlinx.coroutines.delay(10)
            }
        }
    }

    // ─── connect ──────────────────────────────────────────────────────────────

    @Test
    fun `connect transitions state machine to connected and sends RequestKeyframe`() = runBlocking {
        connectPipeline()
        assertEquals(ViewerState.Connected(sampleServer), pipeline.stateMachine.currentState)
        coVerify { controlConnection.send(ControlMessage.RequestKeyframe(streamId = 0)) }
    }

    // ─── handleControlMessage — ServerHello with null activeStream ─────────────

    @Test
    fun `ServerHello with null activeStream does not begin streaming`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(ControlMessage.ServerHello(serverVersion = 1, activeStream = null))
        // Allow the collect coroutine to process the message
        kotlinx.coroutines.delay(100)
        assertEquals(ViewerState.Connected(sampleServer), pipeline.stateMachine.currentState)
        verify(exactly = 0) { udpTransportReceiver.start(any()) }
    }

    // ─── handleControlMessage — ServerHello with non-null activeStream ─────────

    @Test
    fun `ServerHello with activeStream begins streaming`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(ControlMessage.ServerHello(serverVersion = 1, activeStream = sampleDescriptor))
        awaitState { it is ViewerState.Streaming }
        val state = pipeline.stateMachine.currentState
        assertTrue(state is ViewerState.Streaming)
        assertEquals(sampleDescriptor.streamId, (state as ViewerState.Streaming).streamId)
    }

    // ─── handleControlMessage — StreamStarted ─────────────────────────────────

    @Test
    fun `StreamStarted message begins streaming`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(
            ControlMessage.StreamStarted(
                streamId = sampleDescriptor.streamId,
                udpPort = sampleDescriptor.udpPort,
                codec = sampleDescriptor.codec,
                width = sampleDescriptor.width,
                height = sampleDescriptor.height,
                framesPerSecond = sampleDescriptor.framesPerSecond
            )
        )
        awaitState { it is ViewerState.Streaming }
        assertTrue(pipeline.stateMachine.currentState is ViewerState.Streaming)
    }

    // ─── handleControlMessage — StreamStopped ─────────────────────────────────

    @Test
    fun `StreamStopped message stops decoder and receiver`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(ControlMessage.ServerHello(serverVersion = 1, activeStream = sampleDescriptor))
        awaitState { it is ViewerState.Streaming }
        incomingFlow.emit(ControlMessage.StreamStopped(streamId = sampleDescriptor.streamId))
        awaitState { it is ViewerState.Connected }
        verify { mediaCodecDecoder.stop() }
        verify { udpTransportReceiver.close() }
        assertTrue(pipeline.stateMachine.currentState is ViewerState.Connected)
    }

    // ─── handleControlMessage — ErrorMessage ──────────────────────────────────

    @Test
    fun `ErrorMessage transitions state machine to error`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(ControlMessage.ErrorMessage(code = "TRANSPORT_FAILURE", message = "socket closed"))
        awaitState { it is ViewerState.Error }
        val state = pipeline.stateMachine.currentState
        assertTrue(state is ViewerState.Error)
        assertEquals("TRANSPORT_FAILURE", (state as ViewerState.Error).code)
    }

    // ─── handleControlMessage — else (Heartbeat) ──────────────────────────────

    @Test
    fun `Heartbeat message is ignored without state change`() = runBlocking {
        connectPipeline()
        val stateBefore = pipeline.stateMachine.currentState
        incomingFlow.emit(ControlMessage.Heartbeat)
        kotlinx.coroutines.delay(100)
        assertEquals(stateBefore, pipeline.stateMachine.currentState)
    }

    // ─── keyframe request callback ────────────────────────────────────────────

    @Test
    fun `keyframe callback sends RequestKeyframe on active connection`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(ControlMessage.ServerHello(serverVersion = 1, activeStream = sampleDescriptor))
        awaitState { it is ViewerState.Streaming }
        withTimeout(3.seconds) {
            while (capturedKeyframeCallback == null) kotlinx.coroutines.delay(10)
        }
        capturedKeyframeCallback!!.invoke()
        coVerify { controlConnection.send(ControlMessage.RequestKeyframe(streamId = sampleDescriptor.streamId)) }
    }

    @Test
    fun `keyframe callback with no active connection is a no-op`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(ControlMessage.ServerHello(serverVersion = 1, activeStream = sampleDescriptor))
        awaitState { it is ViewerState.Streaming }
        withTimeout(3.seconds) {
            while (capturedKeyframeCallback == null) kotlinx.coroutines.delay(10)
        }
        // Disconnect clears the controlConnection reference
        pipeline.disconnect()
        // Invoking the callback after disconnect must not crash
        capturedKeyframeCallback!!.invoke()
        // Only the initial RequestKeyframe(0) was sent during connect(); stream-level keyframe was not sent
        coVerify(exactly = 1) { controlConnection.send(ControlMessage.RequestKeyframe(streamId = 0)) }
    }

    // ─── disconnect — with active components ──────────────────────────────────

    @Test
    fun `disconnect with active decoder and receiver stops and clears them`() = runBlocking {
        connectPipeline()
        incomingFlow.emit(ControlMessage.ServerHello(serverVersion = 1, activeStream = sampleDescriptor))
        awaitState { it is ViewerState.Streaming }
        // Wait for beginStreaming to fully complete: the state machine transitions to Streaming
        // before decoder and transportReceiver fields are assigned. We wait until the factory
        // callback is captured (set just before start()), then yield once more for the
        // decoder = newDecoder field assignment that follows.
        withTimeout(3.seconds) {
            while (capturedKeyframeCallback == null) kotlinx.coroutines.delay(10)
        }
        kotlinx.coroutines.delay(50)
        pipeline.disconnect()
        verify { mediaCodecDecoder.stop() }
        verify { udpTransportReceiver.close() }
        verify { controlConnection.close() }
        assertTrue(pipeline.stateMachine.currentState is ViewerState.Disconnected)
    }

    // ─── disconnect — with no decoder or receiver (connect, no stream started) ─

    @Test
    fun `disconnect with no active decoder or receiver skips their teardown`() = runBlocking {
        connectPipeline()
        // No stream started — decoder and receiver are null
        pipeline.disconnect()
        verify(exactly = 0) { mediaCodecDecoder.stop() }
        verify(exactly = 0) { udpTransportReceiver.close() }
        verify { controlConnection.close() }
        assertTrue(pipeline.stateMachine.currentState is ViewerState.Disconnected)
    }

    // ─── disconnect — without any prior connect ────────────────────────────────

    @Test
    fun `disconnect without prior connect is a no-op for connection teardown`() {
        // controlConnection is null — must not throw
        pipeline.disconnect()
        verify(exactly = 0) { controlConnection.close() }
        assertTrue(pipeline.stateMachine.currentState is ViewerState.Disconnected)
    }

    // ─── branch coverage: hostAddress null fallback ───────────────────────────

    @Test
    fun `connect uses host toString when hostAddress is null`() = runBlocking {
        // Construct a mock InetAddress that returns null for hostAddress so the
        // '?: server.host.toString()' branch in connect() is exercised.
        val hostWithNullAddress: InetAddress = mockk()
        every { hostWithNullAddress.hostAddress } returns null
        every { hostWithNullAddress.toString() } returns "mock-host"

        val serverWithNullHostAddress = ServerInformation(
            hostname = "nullhost",
            host = hostWithNullAddress,
            controlPort = 51000,
            protocolMajorVersion = 1,
            protocolMinorRevision = 1
        )

        // Capture which host string was passed to the controlClientFactory
        var capturedHost: String? = null
        val piplineWithCapture = ViewerPipeline(
            frameSink = frameSink,
            displayCapabilities = DisplayCapabilities(3840, 2160, listOf("h264")),
            controlClientFactory = { host, _, _ ->
                capturedHost = host
                controlClient
            },
            udpTransportReceiverFactory = { _, _ -> udpTransportReceiver },
            mediaCodecDecoderFactory = { _, onKeyframeRequested ->
                capturedKeyframeCallback = onKeyframeRequested
                mediaCodecDecoder
            }
        )

        piplineWithCapture.stateMachine.reduce(ViewerEvent.StartDiscovery)
        piplineWithCapture.connect(testScope, serverWithNullHostAddress)

        assertEquals("mock-host", capturedHost)
    }

    // ─── branch coverage: StreamStopped when decoder/receiver already null ────

    @Test
    fun `StreamStopped when no stream active is handled without crash`() = runBlocking {
        // Arrive at Connected without ever starting a stream, so decoder and
        // transportReceiver are null. The null branches of decoder?.stop() and
        // transportReceiver?.close() inside handleControlMessage must be exercised.
        connectPipeline()
        // At this point state is Connected, decoder == null, transportReceiver == null
        incomingFlow.emit(ControlMessage.StreamStopped(streamId = 0))
        // The state machine's StreamStopped from Connected → stays Connected (invalid transition)
        kotlinx.coroutines.delay(100)
        // Neither stop() nor close() should have been called
        verify(exactly = 0) { mediaCodecDecoder.stop() }
        verify(exactly = 0) { udpTransportReceiver.close() }
    }
}
