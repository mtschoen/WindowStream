package com.mtschoen.windowstream.viewer.observability

enum class StageStatus { Pending, InProgress, Ok, Warning, Error }

data class StreamRowState(
    val openStream: StageStatus = StageStatus.Pending,
    val openStreamError: String? = null,
    val udpArriving: StageStatus = StageStatus.Pending,
    val udpFirstDelayMs: Long? = null,
    val decoder: StageStatus = StageStatus.Pending,
    val decoderError: String? = null,
    val presenting: StageStatus = StageStatus.Pending,
    val fps: Double? = null,
)

data class ViewerState(
    val discovery: StageStatus = StageStatus.Pending,
    val discoveredServer: String? = null,
    val tcpConnect: StageStatus = StageStatus.Pending,
    val tcpConnectError: String? = null,
    val serverHello: StageStatus = StageStatus.Pending,
    val windowCount: Int = 0,
    val streams: Map<Int, StreamRowState> = emptyMap(),
)

class ViewerStateReducer {
    var state: ViewerState = ViewerState()
        private set

    fun apply(event: PipelineEvent) {
        state = when (event) {
            is PipelineEvent.DiscoveryStarted -> state.copy(discovery = StageStatus.InProgress)
            is PipelineEvent.DiscoveryResultReceived -> state.copy(
                discovery = StageStatus.Ok,
                discoveredServer = "${event.hostname} (${event.address}:${event.port})",
            )
            is PipelineEvent.DiscoveryTimedOut -> state.copy(discovery = StageStatus.Warning)
            is PipelineEvent.TcpConnecting -> state.copy(tcpConnect = StageStatus.InProgress)
            is PipelineEvent.TcpConnected -> state.copy(tcpConnect = StageStatus.Ok)
            is PipelineEvent.TcpConnectFailed -> state.copy(
                tcpConnect = StageStatus.Error,
                tcpConnectError = event.cause.message,
            )
            is PipelineEvent.ServerHelloReceived -> state.copy(
                serverHello = StageStatus.Ok,
                windowCount = event.windowCount,
            )
            is PipelineEvent.OpenStreamSent -> state.copy(
                streams = state.streams + (-1 to (state.streams[-1] ?: StreamRowState()).copy(
                    openStream = StageStatus.InProgress,
                )),
            )
            is PipelineEvent.StreamOpened -> state.copy(
                streams = (state.streams - (-1)) + (event.sid to StreamRowState(openStream = StageStatus.Ok)),
            )
            is PipelineEvent.StreamRefused -> {
                val existing = state.streams[event.sid] ?: state.streams[-1] ?: StreamRowState()
                state.copy(streams = (state.streams - (-1)) + (event.sid to existing.copy(
                    openStream = StageStatus.Error,
                    openStreamError = event.message,
                )))
            }
            is PipelineEvent.StreamStopped -> state.copy(streams = state.streams - event.sid)
            is PipelineEvent.UdpFirstPacketReceived -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    udpArriving = StageStatus.Ok,
                    udpFirstDelayMs = event.delayMs,
                )))
            }
            is PipelineEvent.UdpStalled -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(udpArriving = StageStatus.Warning)))
            }
            is PipelineEvent.DecoderStarted -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(decoder = StageStatus.Ok)))
            }
            is PipelineEvent.DecoderFailed -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    decoder = StageStatus.Error,
                    decoderError = event.cause.message,
                )))
            }
            is PipelineEvent.FramesPresenting -> {
                val row = state.streams[event.sid] ?: return
                state.copy(streams = state.streams + (event.sid to row.copy(
                    presenting = StageStatus.Ok,
                    fps = event.fps,
                )))
            }
            else -> state
        }
    }
}
