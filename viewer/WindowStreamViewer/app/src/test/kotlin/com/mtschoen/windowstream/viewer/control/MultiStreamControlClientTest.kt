package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.onStart
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

/**
 * Unit tests for [MultiStreamControlClient] and [MultiStreamControlConnection] using a
 * real loopback TCP socket pair as the fake server, following the pattern established
 * in [ControlClientTest].
 */
class MultiStreamControlClientTest {

    private val capabilities = DisplayCapabilities(3840, 2160, listOf("h264"))

    private fun loopbackAddress(): String =
        InetAddress.getLoopbackAddress().hostAddress ?: "127.0.0.1"

    /** Creates a [MultiStreamControlClient] configured for the loopback server [port]. */
    private fun clientFor(
        port: Int,
        heartbeatSchedulerFactory: () -> HeartbeatScheduler = {
            HeartbeatScheduler(
                sendInterval = 60.seconds,
                silenceTimeout = 120.seconds,
                currentTimeMilliseconds = { System.currentTimeMillis() }
            )
        }
    ) = MultiStreamControlClient(
        host = loopbackAddress(),
        port = port,
        displayCapabilities = capabilities,
        heartbeatSchedulerFactory = heartbeatSchedulerFactory
    )

    // ─── Helper: fake server that performs the initial handshake ──────────────────

    /**
     * Writes a length-prefixed JSON [message] to [output].
     */
    private fun writeMessage(output: java.io.OutputStream, message: ControlMessage) {
        val payload = ProtocolSerialization.json
            .encodeToString(ControlMessage.serializer(), message)
            .toByteArray(Charsets.UTF_8)
        LengthPrefixFraming.writeFrame(output, payload)
    }

    /**
     * Reads and decodes the next length-prefixed JSON message from [input].
     */
    private fun readMessage(input: java.io.InputStream): ControlMessage {
        val frame = LengthPrefixFraming.readFrame(input) ?: error("server read EOF")
        return ProtocolSerialization.json
            .decodeFromString(ControlMessage.serializer(), String(frame, Charsets.UTF_8))
    }

    /**
     * Opens a server socket, accepts one client, completes the HELLO → SERVER_HELLO →
     * VIEWER_READY handshake, then invokes [serverBlock] with the established socket.
     *
     * Returns the [Job] and the bound port.
     */
    private fun startFakeServer(
        scope: CoroutineScope,
        serverHello: ControlMessage.ServerHello = ControlMessage.ServerHello(
            serverVersion = 2,
            udpPort = 5000,
            windows = emptyList()
        ),
        serverBlock: suspend (socket: Socket, input: java.io.InputStream, output: java.io.OutputStream) -> Unit
    ): Pair<ServerSocket, Job> {
        val serverSocket = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val job = scope.launch(Dispatchers.IO) {
            val clientSocket: Socket = serverSocket.accept()
            clientSocket.use { socket ->
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                // Consume HELLO
                readMessage(input)
                // Send SERVER_HELLO
                writeMessage(output, serverHello)
                // Consume VIEWER_READY
                readMessage(input)
                serverBlock(socket, input, output)
            }
        }
        return serverSocket to job
    }

    // ─── Tests ────────────────────────────────────────────────────────────────────

    @Test
    fun `connect returns correct serverHello fields and viewerUdpPort`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val expectedHello = ControlMessage.ServerHello(
            serverVersion = 2,
            udpPort = 5500,
            windows = listOf(
                WindowDescriptor(
                    windowId = 99u,
                    hwnd = 1234L,
                    processId = 100,
                    processName = "notepad",
                    title = "Untitled",
                    physicalWidth = 800,
                    physicalHeight = 600
                )
            )
        )
        val (serverSocket, serverJob) = startFakeServer(scope, serverHello = expectedHello) { _, _, _ ->
            // server stays open until client closes
            delay(2000)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }

        assertEquals(2, connection.serverHello.serverVersion)
        assertEquals(5500, connection.serverHello.udpPort)
        assertEquals(1, connection.serverHello.windows.size)
        assertEquals(99u.toULong(), connection.serverHello.windows[0].windowId)
        // viewerUdpPort property must be accessible (port 0 when transport layer hasn't bound)
        assertEquals(0, connection.viewerUdpPort)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `openStream returns Opened when server replies STREAM_STARTED`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, output ->
            // Receive OPEN_STREAM
            val openStream = readMessage(input)
            assertTrue(openStream is ControlMessage.OpenStream)
            val windowId = (openStream as ControlMessage.OpenStream).windowId
            // Reply STREAM_STARTED
            writeMessage(
                output,
                ControlMessage.StreamStarted(
                    streamId = 10,
                    windowId = windowId,
                    codec = "h264",
                    width = 1920,
                    height = 1080,
                    framesPerSecond = 60
                )
            )
            delay(2000)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }

        val lifecycleFlow = connection.openStream(windowId = 1u, scope = scope)
        val event = withTimeout(3.seconds) { lifecycleFlow.first() }

        assertTrue(event is StreamLifecycleEvent.Opened)
        val opened = event as StreamLifecycleEvent.Opened
        assertEquals(10, opened.streamId)
        assertEquals(1u.toULong(), opened.windowId)
        assertEquals(1920, opened.width)
        assertEquals(1080, opened.height)
        assertEquals(60, opened.framesPerSecond)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `two parallel openStream calls get distinct streamIds`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, output ->
            // Handle first OPEN_STREAM
            val first = readMessage(input) as ControlMessage.OpenStream
            writeMessage(
                output,
                ControlMessage.StreamStarted(
                    streamId = 1,
                    windowId = first.windowId,
                    codec = "h264",
                    width = 800,
                    height = 600,
                    framesPerSecond = 30
                )
            )
            // Handle second OPEN_STREAM
            val second = readMessage(input) as ControlMessage.OpenStream
            writeMessage(
                output,
                ControlMessage.StreamStarted(
                    streamId = 2,
                    windowId = second.windowId,
                    codec = "h264",
                    width = 1280,
                    height = 720,
                    framesPerSecond = 60
                )
            )
            delay(2000)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }

        val flowOne = connection.openStream(windowId = 10u, scope = scope)
        // Stagger slightly so server sees requests in order
        val flowTwo = connection.openStream(windowId = 20u, scope = scope)

        val eventOne = withTimeout(3.seconds) { flowOne.first() }
        val eventTwo = withTimeout(3.seconds) { flowTwo.first() }

        assertTrue(eventOne is StreamLifecycleEvent.Opened)
        assertTrue(eventTwo is StreamLifecycleEvent.Opened)
        val openedOne = eventOne as StreamLifecycleEvent.Opened
        val openedTwo = eventTwo as StreamLifecycleEvent.Opened

        // Each openStream should get a response matching its own windowId
        assertEquals(10u.toULong(), openedOne.windowId)
        assertEquals(20u.toULong(), openedTwo.windowId)
        // And they should have different streamIds
        assertTrue(openedOne.streamId != openedTwo.streamId)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `StreamStopped from server causes lifecycle flow to emit Stopped then complete`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, output ->
            // Handle OPEN_STREAM
            val open = readMessage(input) as ControlMessage.OpenStream
            writeMessage(
                output,
                ControlMessage.StreamStarted(
                    streamId = 7,
                    windowId = open.windowId,
                    codec = "h264",
                    width = 1920,
                    height = 1080,
                    framesPerSecond = 60
                )
            )
            // Consume CLOSE_STREAM sent by client
            readMessage(input)
            // Server sends STREAM_STOPPED
            writeMessage(
                output,
                ControlMessage.StreamStopped(
                    streamId = 7,
                    reason = StreamStoppedReason.ClosedByViewer
                )
            )
            delay(1000)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }

        val lifecycleFlow = connection.openStream(windowId = 5u, scope = scope)

        // Wait for Opened
        val opened = withTimeout(3.seconds) { lifecycleFlow.first() }
        assertTrue(opened is StreamLifecycleEvent.Opened)

        // Close the stream
        connection.closeStream(streamId = 7)

        // Collect the Stopped event (flow should also complete)
        val events = withTimeout(3.seconds) { lifecycleFlow.take(1).toList() }
        assertEquals(1, events.size)
        val stopped = events[0]
        assertTrue(stopped is StreamLifecycleEvent.Stopped)
        val stoppedEvent = stopped as StreamLifecycleEvent.Stopped
        assertEquals(7, stoppedEvent.reason.streamId)
        assertEquals(StreamStoppedReason.ClosedByViewer, stoppedEvent.reason.reason)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `ErrorMessage from server for OPEN_STREAM causes lifecycle flow to emit Refused`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, output ->
            // Consume OPEN_STREAM
            readMessage(input)
            // Reply with an error (encoder at capacity)
            writeMessage(
                output,
                ControlMessage.ErrorMessage(
                    code = "EncoderCapacity",
                    message = "encoder at maximum stream count"
                )
            )
            delay(1000)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }

        val lifecycleFlow = connection.openStream(windowId = 3u, scope = scope)
        val event = withTimeout(3.seconds) { lifecycleFlow.first() }

        assertTrue(event is StreamLifecycleEvent.Refused)
        val refused = event as StreamLifecycleEvent.Refused
        assertEquals("EncoderCapacity", refused.errorCode)
        assertEquals("encoder at maximum stream count", refused.message)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `default constructor parameters work with a real loopback server`() = runBlocking {
        // Exercises the default heartbeatSchedulerFactory and socketFactory code paths
        // that remain uncovered when all other tests supply their own factories.
        val scope = CoroutineScope(Dispatchers.IO)
        val (serverSocket, serverJob) = startFakeServer(scope) { _, _, _ ->
            delay(500)
        }

        // Use the 3-param constructor — heartbeatSchedulerFactory and socketFactory are defaults
        val client = MultiStreamControlClient(
            host = loopbackAddress(),
            port = serverSocket.localPort,
            displayCapabilities = capabilities
        )
        val connection = withTimeout(3.seconds) { client.connect(scope) }
        assertEquals(2, connection.serverHello.serverVersion)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `send transmits arbitrary messages to the server`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val receivedAfterHandshake: MutableList<ControlMessage> = mutableListOf()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, _ ->
            // Read one message the client sends after handshake
            val message = readMessage(input)
            receivedAfterHandshake.add(message)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }

        connection.send(ControlMessage.ListWindows)

        serverJob.join()
        serverSocket.close()
        connection.close()

        assertEquals(1, receivedAfterHandshake.size)
        assertTrue(receivedAfterHandshake[0] is ControlMessage.ListWindows)
    }

    @Test
    fun `pauseStream sends PAUSE_STREAM to server`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val received: MutableList<ControlMessage> = mutableListOf()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, _ ->
            received.add(readMessage(input))
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }
        connection.pauseStream(streamId = 4)

        serverJob.join()
        serverSocket.close()
        connection.close()

        assertEquals(1, received.size)
        assertTrue(received[0] is ControlMessage.PauseStream)
        assertEquals(4, (received[0] as ControlMessage.PauseStream).streamId)
    }

    @Test
    fun `resumeStream sends RESUME_STREAM to server`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val received: MutableList<ControlMessage> = mutableListOf()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, _ ->
            received.add(readMessage(input))
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }
        connection.resumeStream(streamId = 5)

        serverJob.join()
        serverSocket.close()
        connection.close()

        assertEquals(1, received.size)
        assertTrue(received[0] is ControlMessage.ResumeStream)
        assertEquals(5, (received[0] as ControlMessage.ResumeStream).streamId)
    }

    @Test
    fun `focusWindow sends FOCUS_WINDOW to server`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val received: MutableList<ControlMessage> = mutableListOf()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, _ ->
            received.add(readMessage(input))
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }
        connection.focusWindow(streamId = 6)

        serverJob.join()
        serverSocket.close()
        connection.close()

        assertEquals(1, received.size)
        assertTrue(received[0] is ControlMessage.FocusWindow)
        assertEquals(6, (received[0] as ControlMessage.FocusWindow).streamId)
    }

    @Test
    fun `sendKeyEvent sends KEY_EVENT to server`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val received: MutableList<ControlMessage> = mutableListOf()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, _ ->
            received.add(readMessage(input))
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }
        connection.sendKeyEvent(streamId = 7, keyCode = 65, isUnicode = false, isDown = true)

        serverJob.join()
        serverSocket.close()
        connection.close()

        assertEquals(1, received.size)
        val keyEvent = received[0]
        assertTrue(keyEvent is ControlMessage.KeyEvent)
        keyEvent as ControlMessage.KeyEvent
        assertEquals(7, keyEvent.streamId)
        assertEquals(65, keyEvent.keyCode)
        assertEquals(false, keyEvent.isUnicode)
        assertEquals(true, keyEvent.isDown)
    }

    @Test
    fun `requestKeyframe sends REQUEST_KEYFRAME to server`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val received: MutableList<ControlMessage> = mutableListOf()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, _ ->
            received.add(readMessage(input))
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }
        connection.requestKeyframe(streamId = 8)

        serverJob.join()
        serverSocket.close()
        connection.close()

        assertEquals(1, received.size)
        assertTrue(received[0] is ControlMessage.RequestKeyframe)
        assertEquals(8, (received[0] as ControlMessage.RequestKeyframe).streamId)
    }

    @Test
    fun `transport failure emits TRANSPORT_FAILURE to incoming`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        // The subscriber signals the gate from its onStart operator (i.e., after the
        // SharedFlow subscription is established) so the server only sends the malformed
        // bytes once the collector is guaranteed to be listening. This avoids the race
        // inherent with replay=0 SharedFlow.
        val subscriberReadyGate = CompletableDeferred<Unit>()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, _, output ->
            subscriberReadyGate.await()
            output.write(byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte()))
            output.flush()
            delay(1000)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(
                port = serverSocket.localPort,
                heartbeatSchedulerFactory = {
                    HeartbeatScheduler(
                        sendInterval = 60.seconds,
                        silenceTimeout = 120.seconds,
                        currentTimeMilliseconds = { System.currentTimeMillis() }
                    )
                }
            ).connect(scope)
        }

        val errorDeferred = scope.async {
            connection.incoming
                .onStart { subscriberReadyGate.complete(Unit) }
                .filter { it is ControlMessage.ErrorMessage }
                .first()
        }

        val errorMessage = withTimeout(3.seconds) { errorDeferred.await() }
        assertTrue(errorMessage is ControlMessage.ErrorMessage)
        assertEquals("TRANSPORT_FAILURE", (errorMessage as ControlMessage.ErrorMessage).code)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `non-routed server messages are emitted on incoming`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        // The subscriber's onStart signals when the SharedFlow subscription is live,
        // so the server only sends WindowAdded after the collector is guaranteed to
        // be receiving — avoids the replay=0 race.
        val subscriberReadyGate = CompletableDeferred<Unit>()
        val (serverSocket, serverJob) = startFakeServer(scope) { _, _, output ->
            subscriberReadyGate.await()
            writeMessage(
                output,
                ControlMessage.WindowAdded(
                    window = WindowDescriptor(
                        windowId = 55u,
                        hwnd = 0L,
                        processId = 0,
                        processName = "explorer",
                        title = "Desktop",
                        physicalWidth = 1920,
                        physicalHeight = 1080
                    )
                )
            )
            delay(1000)
        }

        val connection = withTimeout(3.seconds) {
            clientFor(serverSocket.localPort).connect(scope)
        }

        val messageDeferred = scope.async {
            connection.incoming
                .onStart { subscriberReadyGate.complete(Unit) }
                .filter { it is ControlMessage.WindowAdded }
                .first()
        }

        val message = withTimeout(3.seconds) { messageDeferred.await() }
        assertTrue(message is ControlMessage.WindowAdded)
        assertEquals(55u.toULong(), (message as ControlMessage.WindowAdded).window.windowId)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `heartbeat timeout emits HEARTBEAT_TIMEOUT on incoming`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val (serverSocket, serverJob) = startFakeServer(scope) { _, input, output ->
            // Keep server alive but silent after handshake
            try { input.read() } catch (_: Exception) { }
        }

        var timeValue = 0L
        val connection = withTimeout(3.seconds) {
            clientFor(
                port = serverSocket.localPort,
                heartbeatSchedulerFactory = {
                    HeartbeatScheduler(
                        sendInterval = 100.milliseconds,
                        silenceTimeout = 300.milliseconds,
                        currentTimeMilliseconds = { timeValue }
                    )
                }
            ).connect(scope)
        }

        // Advance fake time past silence timeout
        timeValue = 500L

        val error = withTimeout(5.seconds) {
            connection.incoming.filter { it is ControlMessage.ErrorMessage }.first()
        }
        assertTrue(error is ControlMessage.ErrorMessage)
        assertEquals("HEARTBEAT_TIMEOUT", (error as ControlMessage.ErrorMessage).code)

        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

}
