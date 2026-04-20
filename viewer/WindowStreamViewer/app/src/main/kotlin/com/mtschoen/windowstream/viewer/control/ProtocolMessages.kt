package com.mtschoen.windowstream.viewer.control

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class DisplayCapabilities(
    val maximumWidth: Int,
    val maximumHeight: Int,
    val supportedCodecs: List<String>
)

@Serializable
data class ActiveStreamDescriptor(
    val streamId: Int,
    val udpPort: Int,
    val codec: String,
    val width: Int,
    val height: Int,
    val framesPerSecond: Int
)

@Serializable
sealed class ControlMessage {
    @Serializable
    @SerialName("HELLO")
    data class Hello(
        val viewerVersion: Int,
        val displayCapabilities: DisplayCapabilities
    ) : ControlMessage()

    @Serializable
    @SerialName("SERVER_HELLO")
    data class ServerHello(
        val serverVersion: Int,
        val activeStream: ActiveStreamDescriptor? = null
    ) : ControlMessage()

    @Serializable
    @SerialName("STREAM_STARTED")
    data class StreamStarted(
        val streamId: Int,
        val udpPort: Int,
        val codec: String,
        val width: Int,
        val height: Int,
        val framesPerSecond: Int
    ) : ControlMessage()

    @Serializable
    @SerialName("STREAM_STOPPED")
    data class StreamStopped(val streamId: Int) : ControlMessage()

    @Serializable
    @SerialName("REQUEST_KEYFRAME")
    data class RequestKeyframe(val streamId: Int) : ControlMessage()

    @Serializable
    @SerialName("HEARTBEAT")
    data object Heartbeat : ControlMessage()

    @Serializable
    @SerialName("ERROR")
    data class ErrorMessage(val code: String, val message: String) : ControlMessage()

    @Serializable
    @SerialName("VIEWER_READY")
    data class ViewerReady(val streamId: Int, val viewerUdpPort: Int) : ControlMessage()
}
