package com.mtschoen.windowstream.viewer.control

import io.mockk.every
import io.mockk.mockk
import io.mockk.verify
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

class ControlClientTest {

    private fun loopbackAddress(): String =
        InetAddress.getLoopbackAddress().hostAddress ?: "127.0.0.1"

    @Test
    fun `sends HELLO on connect and receives SERVER_HELLO`() = runBlocking {
        val serverSocket = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val port: Int = serverSocket.localPort
        val serverJob: Job = launch(Dispatchers.IO) {
            val clientConnection: Socket = serverSocket.accept()
            clientConnection.use { socket ->
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                val helloBytes: ByteArray = LengthPrefixFraming.readFrame(input) ?: error("no hello")
                val helloJson = String(helloBytes, Charsets.UTF_8)
                assertTrue(helloJson.contains("HELLO"))
                val serverHelloPayload: String = ProtocolSerialization.json.encodeToString(
                    ControlMessage.serializer(),
                    ControlMessage.ServerHello(serverVersion = 1, activeStream = null)
                )
                LengthPrefixFraming.writeFrame(output, serverHelloPayload.toByteArray(Charsets.UTF_8))
            }
        }
        val client = ControlClient(
            host = loopbackAddress(),
            port = port,
            displayCapabilities = DisplayCapabilities(3840, 2160, listOf("h264"))
        )
        val scope = CoroutineScope(Dispatchers.IO)
        val connection = client.connect(scope)
        val firstMessage: ControlMessage = withTimeout(2.seconds) { connection.incoming.first() }
        assertTrue(firstMessage is ControlMessage.ServerHello)
        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `send writes a framed message to the server`() = runBlocking {
        val serverSocket = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val port: Int = serverSocket.localPort
        val receivedMessages: MutableList<String> = mutableListOf()
        val serverJob: Job = launch(Dispatchers.IO) {
            val clientConnection: Socket = serverSocket.accept()
            clientConnection.use { socket ->
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                // Read HELLO
                LengthPrefixFraming.readFrame(input) ?: error("no hello")
                // Send SERVER_HELLO back
                val serverHelloPayload: String = ProtocolSerialization.json.encodeToString(
                    ControlMessage.serializer(),
                    ControlMessage.ServerHello(serverVersion = 1, activeStream = null)
                )
                LengthPrefixFraming.writeFrame(output, serverHelloPayload.toByteArray(Charsets.UTF_8))
                // Read the next message sent by client
                val nextFrame: ByteArray = LengthPrefixFraming.readFrame(input) ?: error("no next frame")
                receivedMessages.add(String(nextFrame, Charsets.UTF_8))
            }
        }
        val client = ControlClient(
            host = loopbackAddress(),
            port = port,
            displayCapabilities = DisplayCapabilities(1920, 1080, listOf("h265"))
        )
        val scope = CoroutineScope(Dispatchers.IO)
        val connection = client.connect(scope)
        // Wait for SERVER_HELLO (replay = 1 on incoming guarantees we don't miss it)
        withTimeout(2.seconds) { connection.incoming.first() }
        // Send a message from client to server
        connection.send(ControlMessage.RequestKeyframe(streamId = 3))
        serverJob.join()
        connection.close()
        serverSocket.close()
        assertTrue(receivedMessages.isNotEmpty())
        assertTrue(receivedMessages[0].contains("REQUEST_KEYFRAME"))
    }

    @Test
    fun `emits TRANSPORT_FAILURE when server sends malformed data`() = runBlocking {
        val serverSocket = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val port: Int = serverSocket.localPort
        val serverJob: Job = launch(Dispatchers.IO) {
            val clientConnection: Socket = serverSocket.accept()
            clientConnection.use { socket ->
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                // Read HELLO
                LengthPrefixFraming.readFrame(input)
                // Write negative-length prefix — triggers MalformedFrameException (non-null message)
                output.write(byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte()))
                output.flush()
            }
        }
        val client = ControlClient(
            host = loopbackAddress(),
            port = port,
            displayCapabilities = DisplayCapabilities(1920, 1080, listOf("h264")),
            heartbeatSchedulerFactory = {
                HeartbeatScheduler(
                    sendInterval = 60.seconds,
                    silenceTimeout = 120.seconds,
                    currentTimeMilliseconds = { System.currentTimeMillis() }
                )
            }
        )
        val scope = CoroutineScope(Dispatchers.IO)
        val connection = client.connect(scope)
        val errorMessage: ControlMessage = withTimeout(2.seconds) {
            connection.incoming.filter { it is ControlMessage.ErrorMessage }.first()
        }
        assertTrue(errorMessage is ControlMessage.ErrorMessage)
        assertEquals("TRANSPORT_FAILURE", (errorMessage as ControlMessage.ErrorMessage).code)
        connection.close()
        serverJob.join()
        serverSocket.close()
    }

    @Test
    fun `TRANSPORT_FAILURE uses fallback text when throwable has null message`() = runBlocking {
        // Build a mock socket whose input stream throws RuntimeException() (null message)
        // after delivering the HELLO write buffer
        val helloOutput = ByteArrayOutputStream()
        val mockSocket = mockk<Socket>(relaxed = true)
        val mockOutput = mockk<OutputStream>(relaxed = true)
        every { mockSocket.getOutputStream() } returns mockOutput
        // Capture what is written to the output stream so we can include it in the fake input
        val writtenPayloads: MutableList<ByteArray> = mutableListOf()
        every { mockSocket.getInputStream() } answers {
            // Return an InputStream that throws RuntimeException() (null message) on first read
            object : java.io.InputStream() {
                override fun read(): Int = throw RuntimeException() // null message
            }
        }
        every { mockOutput.write(any<Int>()) } answers { helloOutput.write(firstArg<Int>()) }
        every { mockOutput.write(any<ByteArray>()) } answers { helloOutput.write(firstArg<ByteArray>()) }
        every { mockOutput.write(any<ByteArray>(), any<Int>(), any<Int>()) } answers {
            helloOutput.write(firstArg<ByteArray>(), secondArg<Int>(), thirdArg<Int>())
        }
        every { mockOutput.flush() } returns Unit

        val client = ControlClient(
            host = "127.0.0.1",
            port = 0,
            displayCapabilities = DisplayCapabilities(1920, 1080, listOf("h264")),
            heartbeatSchedulerFactory = {
                HeartbeatScheduler(
                    sendInterval = 60.seconds,
                    silenceTimeout = 120.seconds,
                    currentTimeMilliseconds = { System.currentTimeMillis() }
                )
            },
            socketFactory = { _, _ -> mockSocket }
        )
        val scope = CoroutineScope(Dispatchers.IO)
        val connection = client.connect(scope)
        val errorMessage: ControlMessage = withTimeout(2.seconds) {
            connection.incoming.filter { it is ControlMessage.ErrorMessage }.first()
        }
        assertTrue(errorMessage is ControlMessage.ErrorMessage)
        assertEquals("TRANSPORT_FAILURE", (errorMessage as ControlMessage.ErrorMessage).code)
        assertEquals("io error", (errorMessage as ControlMessage.ErrorMessage).message)
        connection.close()
    }

    @Test
    fun `emits HEARTBEAT_TIMEOUT and closes when heartbeat scheduler fires timeout`() = runBlocking {
        val serverSocket = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val port: Int = serverSocket.localPort
        val serverJob: Job = launch(Dispatchers.IO) {
            val clientConnection: Socket = serverSocket.accept()
            clientConnection.use { socket ->
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                // Read HELLO
                LengthPrefixFraming.readFrame(input)
                // Send SERVER_HELLO
                val serverHelloPayload: String = ProtocolSerialization.json.encodeToString(
                    ControlMessage.serializer(),
                    ControlMessage.ServerHello(serverVersion = 1, activeStream = null)
                )
                LengthPrefixFraming.writeFrame(output, serverHelloPayload.toByteArray(Charsets.UTF_8))
                // Keep server alive but don't send any more messages (simulate silence)
                // Wait until the client closes the connection
                try { input.read() } catch (_: Exception) { }
            }
        }
        // Use a scheduler with very short timeout to trigger quickly
        var timeValue = 0L
        val client = ControlClient(
            host = loopbackAddress(),
            port = port,
            displayCapabilities = DisplayCapabilities(1920, 1080, listOf("h264")),
            heartbeatSchedulerFactory = {
                HeartbeatScheduler(
                    sendInterval = 100.milliseconds,
                    silenceTimeout = 300.milliseconds,
                    currentTimeMilliseconds = { timeValue }
                )
            }
        )
        val scope = CoroutineScope(Dispatchers.IO)
        val connection = client.connect(scope)
        // Wait for SERVER_HELLO
        withTimeout(2.seconds) { connection.incoming.first { it is ControlMessage.ServerHello } }
        // Advance fake time past timeout threshold so scheduler fires timeout on next tick
        timeValue = 400L
        // Await HEARTBEAT_TIMEOUT error message from incoming
        val errorMessage: ControlMessage = withTimeout(5.seconds) {
            connection.incoming.filter { it is ControlMessage.ErrorMessage }.first()
        }
        assertTrue(errorMessage is ControlMessage.ErrorMessage)
        assertEquals("HEARTBEAT_TIMEOUT", (errorMessage as ControlMessage.ErrorMessage).code)
        connection.close()
        serverJob.cancelAndJoin()
        serverSocket.close()
    }

    @Test
    fun `heartbeat scheduler sends heartbeat frames to server`() = runBlocking {
        val serverSocket = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val port: Int = serverSocket.localPort
        val receivedFrames: MutableList<String> = mutableListOf()
        val serverJob: Job = launch(Dispatchers.IO) {
            val clientConnection: Socket = serverSocket.accept()
            clientConnection.use { socket ->
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                // Read HELLO
                LengthPrefixFraming.readFrame(input)
                // Send SERVER_HELLO
                val serverHelloPayload: String = ProtocolSerialization.json.encodeToString(
                    ControlMessage.serializer(),
                    ControlMessage.ServerHello(serverVersion = 1, activeStream = null)
                )
                LengthPrefixFraming.writeFrame(output, serverHelloPayload.toByteArray(Charsets.UTF_8))
                // Read the heartbeat frame the client sends
                val heartbeatFrame: ByteArray = LengthPrefixFraming.readFrame(input) ?: error("no heartbeat")
                receivedFrames.add(String(heartbeatFrame, Charsets.UTF_8))
            }
        }
        // Use short send interval so heartbeat fires quickly
        var timeValue = 0L
        val client = ControlClient(
            host = loopbackAddress(),
            port = port,
            displayCapabilities = DisplayCapabilities(1920, 1080, listOf("h264")),
            heartbeatSchedulerFactory = {
                HeartbeatScheduler(
                    sendInterval = 100.milliseconds,
                    silenceTimeout = 60.seconds,
                    currentTimeMilliseconds = { timeValue }
                )
            }
        )
        val scope = CoroutineScope(Dispatchers.IO)
        val connection = client.connect(scope)
        // Wait for SERVER_HELLO
        withTimeout(2.seconds) { connection.incoming.first() }
        // Advance time past the send interval to trigger heartbeat on next scheduler tick
        timeValue = 200L
        serverJob.join()
        connection.close()
        serverSocket.close()
        assertTrue(receivedFrames.isNotEmpty())
        assertTrue(receivedFrames[0].contains("HEARTBEAT"))
    }
}
