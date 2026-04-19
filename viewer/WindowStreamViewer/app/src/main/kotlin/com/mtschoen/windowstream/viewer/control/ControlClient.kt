package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.InetAddress
import java.net.Socket
import kotlin.time.Duration.Companion.seconds

class ControlConnection internal constructor(
    private val socket: Socket,
    private val readerJob: Job,
    private val heartbeatJob: Job,
    val incoming: Flow<ControlMessage>,
    private val writeLock: Any,
    private val outgoingLambda: suspend (ControlMessage) -> Unit
) {
    suspend fun send(message: ControlMessage) {
        outgoingLambda(message)
    }

    fun close() {
        readerJob.cancel()
        heartbeatJob.cancel()
        runCatching { socket.close() }
    }
}

class ControlClient(
    private val host: String,
    private val port: Int,
    private val displayCapabilities: DisplayCapabilities,
    private val viewerVersion: Int = 1,
    private val heartbeatSchedulerFactory: () -> HeartbeatScheduler = {
        HeartbeatScheduler(
            sendInterval = 2.seconds,
            silenceTimeout = 6.seconds,
            currentTimeMilliseconds = { System.currentTimeMillis() }
        )
    },
    private val socketFactory: (String, Int) -> Socket = { socketHost, socketPort ->
        Socket(InetAddress.getByName(socketHost), socketPort)
    }
) {
    suspend fun connect(scope: CoroutineScope): ControlConnection = withContext(Dispatchers.IO) {
        val socket = socketFactory(host, port)
        socket.tcpNoDelay = true
        val input = socket.getInputStream()
        val output = socket.getOutputStream()
        val incomingFlow = MutableSharedFlow<ControlMessage>(
            replay = 1,
            extraBufferCapacity = 32,
            onBufferOverflow = BufferOverflow.SUSPEND
        )
        val writeLock = Any()

        suspend fun sendMessage(message: ControlMessage) = withContext(Dispatchers.IO) {
            val payload: ByteArray = ProtocolSerialization.json
                .encodeToString(ControlMessage.serializer(), message)
                .toByteArray(Charsets.UTF_8)
            synchronized(writeLock) {
                LengthPrefixFraming.writeFrame(output, payload)
            }
        }

        sendMessage(
            ControlMessage.Hello(viewerVersion = viewerVersion, displayCapabilities = displayCapabilities)
        )

        val heartbeatScheduler = heartbeatSchedulerFactory()

        val readerJob = scope.launch(Dispatchers.IO) {
            try {
                while (true) {
                    val frame: ByteArray = LengthPrefixFraming.readFrame(input) ?: break
                    val message: ControlMessage = ProtocolSerialization.json
                        .decodeFromString(ControlMessage.serializer(), String(frame, Charsets.UTF_8))
                    heartbeatScheduler.recordIncomingActivity()
                    incomingFlow.emit(message)
                }
            } catch (throwable: Throwable) {
                incomingFlow.emit(
                    ControlMessage.ErrorMessage("TRANSPORT_FAILURE", throwable.message ?: "io error")
                )
            }
        }

        val heartbeatJob = scope.launch(Dispatchers.IO) {
            heartbeatScheduler.run(
                sendHeartbeat = { sendMessage(ControlMessage.Heartbeat) },
                onTimeout = {
                    incomingFlow.emit(ControlMessage.ErrorMessage("HEARTBEAT_TIMEOUT", "server silent > 6s"))
                    runCatching { socket.close() }
                }
            )
        }

        ControlConnection(
            socket = socket,
            readerJob = readerJob,
            heartbeatJob = heartbeatJob,
            incoming = incomingFlow.asSharedFlow(),
            writeLock = writeLock,
            outgoingLambda = ::sendMessage
        )
    }
}
