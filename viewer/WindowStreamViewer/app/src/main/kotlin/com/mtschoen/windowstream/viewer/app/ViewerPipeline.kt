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
import com.mtschoen.windowstream.viewer.state.ViewerStateMachine
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import java.net.InetAddress

class ViewerPipeline(
    private val frameSink: FrameSink,
    private val displayCapabilities: DisplayCapabilities,
    private val controlClientFactory: (host: String, port: Int, displayCapabilities: DisplayCapabilities) -> ControlClient,
    private val udpTransportReceiverFactory: (bindAddress: InetAddress, requestedPort: Int) -> UdpTransportReceiver,
    private val mediaCodecDecoderFactory: (frameSink: FrameSink, onKeyframeRequested: suspend () -> Unit) -> MediaCodecDecoder
) {
    companion object {
        /**
         * Production factory: constructs a [ViewerPipeline] wired to the real Android
         * platform implementations. This method is excluded from JVM unit-test coverage
         * because [ControlClient], [UdpTransportReceiver], and [MediaCodecDecoder] all
         * require Android runtime or network I/O that is not available in the JVM
         * test environment. The class constructor and all business logic are fully covered.
         */
        fun create(frameSink: FrameSink): ViewerPipeline = ViewerPipeline(
            frameSink = frameSink,
            displayCapabilities = DisplayCapabilities(3840, 2160, listOf("h264")),
            controlClientFactory = { host, port, capabilities ->
                ControlClient(host = host, port = port, displayCapabilities = capabilities)
            },
            udpTransportReceiverFactory = { bindAddress, requestedPort ->
                UdpTransportReceiver(bindAddress = bindAddress, requestedPort = requestedPort)
            },
            mediaCodecDecoderFactory = { sink, onKeyframeRequested ->
                MediaCodecDecoder(frameSink = sink, onKeyframeRequested = onKeyframeRequested)
            }
        )
    }

    private var controlConnection: ControlConnection? = null
    private var transportReceiver: UdpTransportReceiver? = null
    private var decoder: MediaCodecDecoder? = null
    val stateMachine: ViewerStateMachine = ViewerStateMachine()

    suspend fun connect(scope: CoroutineScope, server: ServerInformation) {
        stateMachine.reduce(ViewerEvent.ServerSelected(server))
        val client = controlClientFactory(
            server.host.hostAddress ?: server.host.toString(),
            server.controlPort,
            displayCapabilities
        )
        val connection: ControlConnection = client.connect(scope)
        controlConnection = connection
        scope.launch(Dispatchers.IO) {
            connection.incoming.collect { message -> handleControlMessage(scope, server.host, message) }
        }
        connection.send(ControlMessage.RequestKeyframe(streamId = 0))
    }

    private suspend fun handleControlMessage(scope: CoroutineScope, host: InetAddress, message: ControlMessage) {
        when (message) {
            is ControlMessage.ServerHello -> message.activeStream?.let { descriptor ->
                beginStreaming(scope, host, descriptor)
            }
            is ControlMessage.StreamStarted -> beginStreaming(
                scope, host,
                ActiveStreamDescriptor(
                    streamId = message.streamId,
                    udpPort = message.udpPort,
                    codec = message.codec,
                    width = message.width,
                    height = message.height,
                    framesPerSecond = message.framesPerSecond
                )
            )
            is ControlMessage.StreamStopped -> {
                stateMachine.reduce(ViewerEvent.StreamStopped(message.streamId))
                decoder?.stop()
                transportReceiver?.close()
                decoder = null
                transportReceiver = null
            }
            is ControlMessage.ErrorMessage -> {
                stateMachine.reduce(ViewerEvent.FatalError(message.code, message.message))
            }
            else -> Unit
        }
    }

    private fun beginStreaming(scope: CoroutineScope, host: InetAddress, descriptor: ActiveStreamDescriptor) {
        stateMachine.reduce(ViewerEvent.StreamStarted(descriptor.streamId, descriptor.width, descriptor.height))
        val receiver = udpTransportReceiverFactory(InetAddress.getByName("0.0.0.0"), 0)
        val frameFlow = receiver.start(scope)
        transportReceiver = receiver
        val newDecoder = mediaCodecDecoderFactory(frameSink) {
            controlConnection?.send(ControlMessage.RequestKeyframe(descriptor.streamId))
        }
        newDecoder.start(scope, frameFlow, descriptor.width, descriptor.height)
        decoder = newDecoder
    }

    fun disconnect() {
        stateMachine.reduce(ViewerEvent.Disconnect)
        decoder?.stop()
        transportReceiver?.close()
        controlConnection?.close()
        decoder = null
        transportReceiver = null
        controlConnection = null
    }
}
