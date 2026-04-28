package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.net.InetAddress
import java.net.Socket
import kotlin.time.Duration.Companion.seconds

/**
 * Lifecycle events for a single stream opened via [MultiStreamControlConnection.openStream].
 */
sealed class StreamLifecycleEvent {
    /**
     * The server accepted the OPEN_STREAM request and the stream is now active.
     */
    data class Opened(
        val streamId: Int,
        val windowId: ULong,
        val width: Int,
        val height: Int,
        val framesPerSecond: Int
    ) : StreamLifecycleEvent()

    /**
     * The server refused the OPEN_STREAM request (capacity limit, window not found, etc.).
     */
    data class Refused(val errorCode: String, val message: String) : StreamLifecycleEvent()

    /**
     * The server stopped a previously opened stream.
     */
    data class Stopped(val reason: ControlMessage.StreamStopped) : StreamLifecycleEvent()
}

/**
 * A live multi-stream connection to a WindowStream server. Obtained via
 * [MultiStreamControlClient.connect].
 *
 * A single background coroutine reads all incoming server messages and routes them to:
 * - [incoming]: every server-initiated message (SharedFlow, replay=0).
 * - Per-windowId mailbox channels: STREAM_STARTED responses awaited by [openStream].
 * - Per-streamId mailbox channels: STREAM_STOPPED events delivered to the per-stream flow.
 * - ERROR responses while an OPEN_STREAM is pending are routed to the matching windowId mailbox.
 */
class MultiStreamControlConnection internal constructor(
    private val socket: Socket,
    private val readerJob: Job,
    private val heartbeatJob: Job,
    private val incomingShared: MutableSharedFlow<ControlMessage>,
    private val writeLock: Any,
    private val sendMessageLambda: suspend (ControlMessage) -> Unit,
    private val windowMailboxes: MutableMap<ULong, Channel<ControlMessage>>,
    private val streamMailboxes: MutableMap<Int, Channel<StreamLifecycleEvent>>,
    private val mailboxLock: Mutex,
    val serverHello: ControlMessage.ServerHello,
    val viewerUdpPort: Int
) {
    /** Every server-initiated message. Replay = 0; collect before calling [openStream]. */
    val incoming: Flow<ControlMessage> = incomingShared.asSharedFlow()

    /** Sends a control message to the server. */
    suspend fun send(message: ControlMessage) {
        sendMessageLambda(message)
    }

    /**
     * Sends OPEN_STREAM for [windowId] and returns a [Flow] of [StreamLifecycleEvent].
     *
     * The first emission is [StreamLifecycleEvent.Opened] on success or
     * [StreamLifecycleEvent.Refused] on capacity / window-not-found errors.
     * Subsequent emissions: [StreamLifecycleEvent.Stopped] when the stream ends.
     *
     * The v2 protocol correlates OPEN_STREAM → STREAM_STARTED by windowId (the server
     * processes requests in order; the next STREAM_STARTED whose windowId matches this
     * request is the response). This implementation registers a per-windowId mailbox
     * before sending the request so no race exists on fast servers.
     *
     * [scope] is used to launch the per-stream lifecycle collector.
     */
    fun openStream(windowId: ULong, scope: CoroutineScope): Flow<StreamLifecycleEvent> {
        val lifecycleChannel = Channel<StreamLifecycleEvent>(capacity = 8)

        scope.launch {
            // Register window mailbox before sending to avoid a race.
            val windowMailbox = Channel<ControlMessage>(capacity = 1)
            mailboxLock.withLock { windowMailboxes[windowId] = windowMailbox }

            sendMessageLambda(ControlMessage.OpenStream(windowId = windowId))

            val response: ControlMessage = windowMailbox.receive()
            mailboxLock.withLock { windowMailboxes.remove(windowId) }

            when (response) {
                is ControlMessage.StreamStarted -> {
                    val streamId = response.streamId
                    // Register stream mailbox so future STREAM_STOPPED events are delivered here.
                    mailboxLock.withLock { streamMailboxes[streamId] = lifecycleChannel }
                    lifecycleChannel.send(
                        StreamLifecycleEvent.Opened(
                            streamId = streamId,
                            windowId = response.windowId,
                            width = response.width,
                            height = response.height,
                            framesPerSecond = response.framesPerSecond
                        )
                    )
                    // lifecycleChannel stays open; STREAM_STOPPED will close it via the mailbox.
                }
                else -> {
                    // The reader only routes StreamStarted or ErrorMessage to window mailboxes,
                    // so this branch handles ErrorMessage responses (capacity / window-not-found).
                    val errorMessage = response as ControlMessage.ErrorMessage
                    lifecycleChannel.send(
                        StreamLifecycleEvent.Refused(
                            errorCode = errorMessage.code,
                            message = errorMessage.message
                        )
                    )
                    lifecycleChannel.close()
                }
            }
        }

        return lifecycleChannel.receiveAsFlow()
    }

    /** Sends CLOSE_STREAM for [streamId]. */
    suspend fun closeStream(streamId: Int) {
        sendMessageLambda(ControlMessage.CloseStream(streamId = streamId))
    }

    /** Sends PAUSE_STREAM for [streamId]. */
    suspend fun pauseStream(streamId: Int) {
        sendMessageLambda(ControlMessage.PauseStream(streamId = streamId))
    }

    /** Sends RESUME_STREAM for [streamId]. */
    suspend fun resumeStream(streamId: Int) {
        sendMessageLambda(ControlMessage.ResumeStream(streamId = streamId))
    }

    /** Sends FOCUS_WINDOW for [streamId]. */
    suspend fun focusWindow(streamId: Int) {
        sendMessageLambda(ControlMessage.FocusWindow(streamId = streamId))
    }

    /** Sends KEY_EVENT for [streamId]. */
    suspend fun sendKeyEvent(
        streamId: Int,
        keyCode: Int,
        isUnicode: Boolean,
        isDown: Boolean
    ) {
        sendMessageLambda(
            ControlMessage.KeyEvent(
                streamId = streamId,
                keyCode = keyCode,
                isUnicode = isUnicode,
                isDown = isDown
            )
        )
    }

    /** Sends REQUEST_KEYFRAME for [streamId]. */
    suspend fun requestKeyframe(streamId: Int) {
        sendMessageLambda(ControlMessage.RequestKeyframe(streamId = streamId))
    }

    /** Closes the connection, cancels background jobs, and releases the socket. */
    suspend fun close() {
        readerJob.cancel()
        heartbeatJob.cancel()
        runCatching { socket.close() }
    }
}

/**
 * Connects to a WindowStream v2 server and supports many parallel stream lifecycles over
 * one TCP control connection. Sends HELLO on connect, awaits SERVER_HELLO, then exposes a
 * [MultiStreamControlConnection] for stream management.
 *
 * This replaces [ControlClient] for v2 multi-window scenarios. [ControlClient] is retained
 * for the legacy DemoActivity path until Phase 5.5.
 */
class MultiStreamControlClient(
    private val host: String,
    private val port: Int,
    private val displayCapabilities: DisplayCapabilities,
    private val viewerVersion: Int = 2,
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
    /**
     * Opens the TCP connection, completes the HELLO/SERVER_HELLO handshake, and returns a
     * [MultiStreamControlConnection] ready for [MultiStreamControlConnection.openStream] calls.
     *
     * Suspends until SERVER_HELLO is received. Throws if the server closes the connection
     * before sending SERVER_HELLO.
     */
    suspend fun connect(scope: CoroutineScope): MultiStreamControlConnection =
        withContext(Dispatchers.IO) {
            val socket = socketFactory(host, port)
            socket.tcpNoDelay = true
            val input = socket.getInputStream()
            val output = socket.getOutputStream()

            val incomingShared = MutableSharedFlow<ControlMessage>(
                replay = 0,
                extraBufferCapacity = 64
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

            // Send HELLO first
            sendMessage(
                ControlMessage.Hello(
                    viewerVersion = viewerVersion,
                    displayCapabilities = displayCapabilities
                )
            )

            // Mailbox state shared between the reader coroutine and connection methods.
            val windowMailboxes: MutableMap<ULong, Channel<ControlMessage>> = mutableMapOf()
            val streamMailboxes: MutableMap<Int, Channel<StreamLifecycleEvent>> = mutableMapOf()
            val mailboxLock = Mutex()

            // Block until SERVER_HELLO arrives (synchronous read before launching reader job).
            val serverHelloFrame: ByteArray =
                LengthPrefixFraming.readFrame(input)
                    ?: error("server closed connection before SERVER_HELLO")
            val serverHelloMessage: ControlMessage = ProtocolSerialization.json
                .decodeFromString(
                    ControlMessage.serializer(),
                    String(serverHelloFrame, Charsets.UTF_8)
                )
            require(serverHelloMessage is ControlMessage.ServerHello) {
                "expected SERVER_HELLO but got ${serverHelloMessage::class.simpleName}"
            }
            val serverHello: ControlMessage.ServerHello = serverHelloMessage

            // Send VIEWER_READY with the viewer UDP port (0 here; real port set by transport layer).
            val viewerUdpPort = 0
            sendMessage(ControlMessage.ViewerReady(viewerUdpPort = viewerUdpPort))

            val heartbeatScheduler = heartbeatSchedulerFactory()

            val readerJob = scope.launch(Dispatchers.IO) {
                try {
                    while (true) {
                        val frame: ByteArray = LengthPrefixFraming.readFrame(input) ?: break
                        val message: ControlMessage = ProtocolSerialization.json
                            .decodeFromString(
                                ControlMessage.serializer(),
                                String(frame, Charsets.UTF_8)
                            )
                        heartbeatScheduler.recordIncomingActivity()

                        // Route to mailboxes before publishing to the shared flow so that
                        // openStream callers receive their response before any shared-flow
                        // subscriber does.
                        when (message) {
                            is ControlMessage.StreamStarted -> {
                                val mailbox: Channel<ControlMessage>? =
                                    mailboxLock.withLock { windowMailboxes[message.windowId] }
                                if (mailbox != null) {
                                    mailbox.trySend(message)
                                }
                            }
                            is ControlMessage.StreamStopped -> {
                                val streamMailbox: Channel<StreamLifecycleEvent>? =
                                    mailboxLock.withLock {
                                        streamMailboxes.remove(message.streamId)
                                    }
                                if (streamMailbox != null) {
                                    streamMailbox.trySend(StreamLifecycleEvent.Stopped(message))
                                    streamMailbox.close()
                                }
                            }
                            is ControlMessage.ErrorMessage -> {
                                // Errors during an OPEN_STREAM are delivered to the pending
                                // window mailbox (if any); otherwise fall through to incomingShared.
                                var routed = false
                                mailboxLock.withLock {
                                    val firstMailbox = windowMailboxes.values.firstOrNull()
                                    if (firstMailbox != null) {
                                        firstMailbox.trySend(message)
                                        routed = true
                                    }
                                }
                                if (!routed) {
                                    incomingShared.emit(message)
                                }
                            }
                            else -> incomingShared.emit(message)
                        }
                    }
                } catch (throwable: Throwable) {
                    incomingShared.emit(
                        ControlMessage.ErrorMessage(
                            "TRANSPORT_FAILURE",
                            throwable.message ?: "io error"
                        )
                    )
                }
            }

            val heartbeatJob = scope.launch(Dispatchers.IO) {
                heartbeatScheduler.run(
                    sendHeartbeat = { sendMessage(ControlMessage.Heartbeat) },
                    onTimeout = {
                        incomingShared.emit(
                            ControlMessage.ErrorMessage("HEARTBEAT_TIMEOUT", "server silent > 6s")
                        )
                        runCatching { socket.close() }
                    }
                )
            }

            MultiStreamControlConnection(
                socket = socket,
                readerJob = readerJob,
                heartbeatJob = heartbeatJob,
                incomingShared = incomingShared,
                writeLock = writeLock,
                sendMessageLambda = ::sendMessage,
                windowMailboxes = windowMailboxes,
                streamMailboxes = streamMailboxes,
                mailboxLock = mailboxLock,
                serverHello = serverHello,
                viewerUdpPort = viewerUdpPort
            )
        }
}
