package com.mtschoen.windowstream.viewer.observability

import com.mtschoen.windowstream.viewer.control.StallCause

enum class Severity { INFO, WARNING, ERROR }

sealed class PipelineEvent(val severity: Severity, val streamId: Int?) {
    object DiscoveryStarted : PipelineEvent(Severity.INFO, null)
    data class DiscoveryResultReceived(val hostname: String, val address: String, val port: Int)
        : PipelineEvent(Severity.INFO, null)
    object DiscoveryTimedOut : PipelineEvent(Severity.WARNING, null)

    data class TcpConnecting(val host: String, val port: Int) : PipelineEvent(Severity.INFO, null)
    data class TcpConnected(val durationMs: Long) : PipelineEvent(Severity.INFO, null)
    data class TcpConnectFailed(val host: String, val port: Int, val cause: Throwable)
        : PipelineEvent(Severity.ERROR, null)

    data class ServerHelloReceived(val windowCount: Int, val udpPort: Int)
        : PipelineEvent(Severity.INFO, null)

    data class OpenStreamSent(val windowId: ULong) : PipelineEvent(Severity.INFO, null)
    data class StreamOpened(val sid: Int, val width: Int, val height: Int) : PipelineEvent(Severity.INFO, sid)
    data class StreamRefused(val sid: Int, val errorCode: String, val message: String)
        : PipelineEvent(Severity.WARNING, sid)
    data class StreamStopped(val sid: Int, val reason: String) : PipelineEvent(Severity.INFO, sid)

    /** The server reports the source stopped rendering; the stream stays alive and may resume. */
    data class SourceStalled(val sid: Int, val cause: StallCause) : PipelineEvent(Severity.WARNING, sid)

    /** The server reports the previously stalled source is rendering again. */
    data class SourceResumed(val sid: Int) : PipelineEvent(Severity.INFO, sid)

    data class UdpBound(val port: Int) : PipelineEvent(Severity.INFO, null)
    data class UdpFirstPacketReceived(val sid: Int, val delayMs: Long) : PipelineEvent(Severity.INFO, sid)
    data class UdpStalled(val sid: Int, val gapMs: Long) : PipelineEvent(Severity.WARNING, sid)

    data class DecoderStarting(val sid: Int, val width: Int, val height: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderStarted(val sid: Int) : PipelineEvent(Severity.INFO, sid)
    data class DecoderFailed(val sid: Int, val cause: Throwable) : PipelineEvent(Severity.ERROR, sid)

    data class SurfaceCreated(val panelIndex: Int) : PipelineEvent(Severity.INFO, null)
    data class SurfaceDestroyed(val panelIndex: Int, val reasonHint: String)
        : PipelineEvent(Severity.INFO, null)

    data class FramesPresenting(val sid: Int, val fps: Double) : PipelineEvent(Severity.INFO, sid)

    object WifiLockAcquired : PipelineEvent(Severity.INFO, null)
    object WifiLockReleased : PipelineEvent(Severity.INFO, null)
}
