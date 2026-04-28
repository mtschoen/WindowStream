package com.mtschoen.windowstream.viewer.control

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

@Serializable
data class DisplayCapabilities(
    val maximumWidth: Int,
    val maximumHeight: Int,
    val supportedCodecs: List<String>
)

/**
 * Window metadata advertised by the server. Mirrors the .NET
 * `WindowStream.Core.Protocol.WindowDescriptor` record.
 *
 * `windowId` is a stable per-server identifier (a `ulong` on the .NET side,
 * `ULong` on the Kotlin side). kotlinx-serialization >= 1.7 encodes `ULong`
 * as a JSON number, matching the .NET wire shape exactly.
 */
@Serializable
data class WindowDescriptor(
    val windowId: ULong,
    val hwnd: Long,
    val processId: Int,
    val processName: String,
    val title: String,
    val physicalWidth: Int,
    val physicalHeight: Int
)

/**
 * Reason the server reports for stopping a stream. Wire form is
 * SCREAMING_SNAKE_CASE — see the .NET `StreamStoppedReasonNames` mapping.
 */
@Serializable
enum class StreamStoppedReason {
    @SerialName("CLOSED_BY_VIEWER") ClosedByViewer,
    @SerialName("WINDOW_GONE") WindowGone,
    @SerialName("ENCODER_FAILED") EncoderFailed,
    @SerialName("CAPTURE_FAILED") CaptureFailed,
    @SerialName("STREAM_HUNG") StreamHung,
    @SerialName("SERVER_SHUTDOWN") ServerShutdown
}

@Serializable
sealed class ControlMessage {
    // ─── existing v1 messages — refactored to v2 shapes ───────────────────────

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
        val udpPort: Int,
        val windows: List<WindowDescriptor>
    ) : ControlMessage()

    @Serializable
    @SerialName("STREAM_STARTED")
    data class StreamStarted(
        val streamId: Int,
        val windowId: ULong,
        val codec: String,
        val width: Int,
        val height: Int,
        val framesPerSecond: Int
    ) : ControlMessage()

    @Serializable
    @SerialName("STREAM_STOPPED")
    data class StreamStopped(
        val streamId: Int,
        val reason: StreamStoppedReason
    ) : ControlMessage()

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
    data class ViewerReady(val viewerUdpPort: Int) : ControlMessage()

    @Serializable
    @SerialName("KEY_EVENT")
    data class KeyEvent(
        val streamId: Int,
        val keyCode: Int,
        val isUnicode: Boolean,
        val isDown: Boolean
    ) : ControlMessage()

    // ─── new v2 messages ──────────────────────────────────────────────────────

    @Serializable
    @SerialName("WINDOW_ADDED")
    data class WindowAdded(val window: WindowDescriptor) : ControlMessage()

    @Serializable
    @SerialName("WINDOW_REMOVED")
    data class WindowRemoved(val windowId: ULong) : ControlMessage()

    @Serializable
    @SerialName("WINDOW_UPDATED")
    data class WindowUpdated(
        val windowId: ULong,
        val title: String? = null,
        val physicalWidth: Int? = null,
        val physicalHeight: Int? = null
    ) : ControlMessage()

    @Serializable
    @SerialName("WINDOW_SNAPSHOT")
    data class WindowSnapshot(val windows: List<WindowDescriptor>) : ControlMessage()

    @Serializable
    @SerialName("LIST_WINDOWS")
    data object ListWindows : ControlMessage()

    @Serializable
    @SerialName("OPEN_STREAM")
    data class OpenStream(val windowId: ULong) : ControlMessage()

    @Serializable
    @SerialName("CLOSE_STREAM")
    data class CloseStream(val streamId: Int) : ControlMessage()

    @Serializable
    @SerialName("PAUSE_STREAM")
    data class PauseStream(val streamId: Int) : ControlMessage()

    @Serializable
    @SerialName("RESUME_STREAM")
    data class ResumeStream(val streamId: Int) : ControlMessage()

    @Serializable
    @SerialName("FOCUS_WINDOW")
    data class FocusWindow(val streamId: Int) : ControlMessage()
}
