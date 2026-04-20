package com.mtschoen.windowstream.viewer.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import java.nio.ByteBuffer

class MediaCodecDecoder(
    private val frameSink: FrameSink,
    private val onKeyframeRequested: suspend () -> Unit
) {
    private var codec: MediaCodec? = null
    private var decodeJob: Job? = null
    private var stallJob: Job? = null
    private val inputBufferIndexChannel: Channel<Int> = Channel(Channel.UNLIMITED)
    @Volatile
    private var cachedParameterSets: ParameterSets? = null
    @Volatile
    private var width: Int = 0
    @Volatile
    private var height: Int = 0

    fun start(scope: CoroutineScope, frameFlow: Flow<EncodedFrame>, expectedWidth: Int, expectedHeight: Int) {
        width = expectedWidth
        height = expectedHeight
        val surface = frameSink.acquireSurface(expectedWidth, expectedHeight)

        val mediaFormat = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, expectedWidth, expectedHeight)
        val newCodec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        newCodec.setCallback(object : MediaCodec.Callback() {
            override fun onInputBufferAvailable(mediaCodec: MediaCodec, inputBufferIndex: Int) {
                inputBufferIndexChannel.trySend(inputBufferIndex)
            }
            override fun onOutputBufferAvailable(
                mediaCodec: MediaCodec, outputBufferIndex: Int, bufferInformation: MediaCodec.BufferInfo
            ) {
                mediaCodec.releaseOutputBuffer(outputBufferIndex, true)
                frameSink.onFrameRendered(bufferInformation.presentationTimeUs)
            }
            override fun onError(mediaCodec: MediaCodec, exception: MediaCodec.CodecException) { /* surfaced via stall */ }
            override fun onOutputFormatChanged(mediaCodec: MediaCodec, newFormat: MediaFormat) { /* no-op */ }
        })
        newCodec.configure(mediaFormat, surface, null, 0)
        newCodec.start()
        codec = newCodec

        val stallMonitor = StallMonitor(
            stallThreshold = kotlin.time.Duration.parse("2s"),
            currentTimeMilliseconds = { System.currentTimeMillis() }
        )
        stallJob = scope.launch(Dispatchers.IO) {
            stallMonitor.run { onKeyframeRequested() }
        }

        decodeJob = scope.launch(Dispatchers.IO) {
            frameFlow.collect { encodedFrame ->
                val parsedParameterSets: ParameterSets = ParameterSetParser.extract(encodedFrame.payload)
                if (parsedParameterSets.sequenceParameterSet != null && parsedParameterSets.pictureParameterSet != null) {
                    cachedParameterSets = parsedParameterSets
                }
                val bufferIndex: Int = inputBufferIndexChannel.receive()
                val inputBuffer: ByteBuffer = newCodec.getInputBuffer(bufferIndex) ?: return@collect
                inputBuffer.clear()
                inputBuffer.put(encodedFrame.payload)
                newCodec.queueInputBuffer(
                    bufferIndex, 0, encodedFrame.payload.size,
                    encodedFrame.presentationTimestampMicroseconds,
                    if (encodedFrame.isIdrFrame) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                )
                stallMonitor.recordFrameRendered()
            }
        }
    }

    fun stop() {
        decodeJob?.cancel()
        stallJob?.cancel()
        codec?.let {
            runCatching { it.stop() }
            runCatching { it.release() }
        }
        codec = null
        frameSink.releaseSurface()
    }
}
