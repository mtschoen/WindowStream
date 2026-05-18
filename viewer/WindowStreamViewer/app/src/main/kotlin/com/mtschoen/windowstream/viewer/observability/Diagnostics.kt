package com.mtschoen.windowstream.viewer.observability

import timber.log.Timber

/**
 * Façade that translates a [PipelineEvent] into a Timber call. Two custom
 * trees (FileLoggingTree, InAppBufferTree) read the payload via a
 * ThreadLocal map populated immediately before the log call.
 *
 * Per-frame markers ([FRAMECOUNT]) deliberately bypass this façade — they
 * live in stderr/logcat to avoid flooding the in-app buffer.
 */
object Diagnostics {

    internal val currentPayload: ThreadLocal<Map<String, Any?>> = ThreadLocal.withInitial { emptyMap() }
    internal val currentEvent: ThreadLocal<PipelineEvent?> = ThreadLocal.withInitial { null }

    fun report(event: PipelineEvent) {
        val tree = Timber.tag(TAG)
        val payload = payloadOf(event)
        currentPayload.set(payload)
        currentEvent.set(event)
        try {
            val message = describe(event)
            when (event.severity) {
                Severity.INFO -> tree.i(message)
                Severity.WARNING -> tree.w(message)
                Severity.ERROR -> tree.e(throwableOf(event), message)
            }
        } finally {
            currentPayload.remove()
            currentEvent.remove()
        }
    }

    private fun describe(event: PipelineEvent): String = event::class.simpleName + ": " + event.toString()

    internal fun throwableOf(event: PipelineEvent): Throwable? = when (event) {
        is PipelineEvent.TcpConnectFailed -> event.cause
        is PipelineEvent.DecoderFailed -> event.cause
        else -> null
    }

    private fun payloadOf(event: PipelineEvent): Map<String, Any?> = buildMap {
        put("eventType", event::class.simpleName)
        put("streamId", event.streamId)
        when (event) {
            is PipelineEvent.DiscoveryResultReceived -> {
                put("hostname", event.hostname); put("address", event.address); put("port", event.port)
            }
            is PipelineEvent.TcpConnecting -> { put("host", event.host); put("port", event.port) }
            is PipelineEvent.TcpConnected -> put("durationMs", event.durationMs)
            is PipelineEvent.TcpConnectFailed -> { put("host", event.host); put("port", event.port) }
            is PipelineEvent.ServerHelloReceived -> {
                put("windowCount", event.windowCount); put("udpPort", event.udpPort)
            }
            is PipelineEvent.OpenStreamSent -> put("windowId", event.windowId.toString())
            is PipelineEvent.StreamOpened -> { put("width", event.width); put("height", event.height) }
            is PipelineEvent.StreamRefused -> { put("errorCode", event.errorCode); put("message", event.message) }
            is PipelineEvent.StreamStopped -> put("reason", event.reason)
            is PipelineEvent.UdpBound -> put("port", event.port)
            is PipelineEvent.UdpFirstPacketReceived -> put("delayMs", event.delayMs)
            is PipelineEvent.UdpStalled -> put("gapMs", event.gapMs)
            is PipelineEvent.DecoderStarting -> { put("width", event.width); put("height", event.height) }
            is PipelineEvent.SurfaceCreated -> put("panelIndex", event.panelIndex)
            is PipelineEvent.SurfaceDestroyed -> {
                put("panelIndex", event.panelIndex); put("reasonHint", event.reasonHint)
            }
            is PipelineEvent.FramesPresenting -> put("fps", event.fps)
            else -> {}
        }
    }

    private const val TAG = "Pipeline"
}
