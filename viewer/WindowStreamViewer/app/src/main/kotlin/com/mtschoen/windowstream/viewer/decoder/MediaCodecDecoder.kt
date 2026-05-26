package com.mtschoen.windowstream.viewer.decoder

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Choreographer
import com.mtschoen.windowstream.viewer.observability.Diagnostics
import com.mtschoen.windowstream.viewer.observability.PipelineEvent
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
    private val onKeyframeRequested: suspend () -> Unit,
    private val streamId: Int = 0,
    private val codecName: String? = null,
    private val outputToSurface: Boolean = true
) {
    private var codec: MediaCodec? = null
    private var decodeJob: Job? = null
    private var stallJob: Job? = null
    private val inputBufferIndexChannel: Channel<Int> = Channel(Channel.UNLIMITED)
    // For stage=present logging — Choreographer.postFrameCallback must run on
    // a Looper thread, but onOutputBufferAvailable fires on MediaCodec's
    // internal callback thread which has no Looper.
    private val mainHandler: Handler = Handler(Looper.getMainLooper())
    @Volatile
    private var cachedParameterSets: ParameterSets? = null
    @Volatile
    private var width: Int = 0
    @Volatile
    private var height: Int = 0

    fun start(scope: CoroutineScope, frameFlow: Flow<EncodedFrame>, expectedWidth: Int, expectedHeight: Int) {
        width = expectedWidth
        height = expectedHeight
        val surface = if (outputToSurface) frameSink.acquireSurface(expectedWidth, expectedHeight) else null

        val mediaFormat = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, expectedWidth, expectedHeight)
        mediaFormat.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        // Schedule the decoder as realtime and ask Qualcomm decoders to run
        // at their maximum clock — the moonlight-android low-latency recipe.
        // Short.MAX_VALUE is the documented sentinel for "as fast as possible".
        mediaFormat.setInteger(MediaFormat.KEY_PRIORITY, 0)
        mediaFormat.setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
        var selectedCodecName = codecName
        if (selectedCodecName == null) {
            val codecList = android.media.MediaCodecList(android.media.MediaCodecList.ALL_CODECS)
            for (codecInfo in codecList.codecInfos) {
                if (codecInfo.isEncoder) continue
                if (!codecInfo.supportedTypes.contains(MediaFormat.MIMETYPE_VIDEO_AVC)) continue
                if (codecInfo.name.contains(".low_latency")) {
                    selectedCodecName = codecInfo.name
                    Log.i("MediaCodecDecoder", "Found explicit low_latency codec variant: $selectedCodecName")
                    break
                }
            }
            if (selectedCodecName == null) {
                selectedCodecName = android.media.MediaCodecList(android.media.MediaCodecList.REGULAR_CODECS).findDecoderForFormat(mediaFormat)
                if (selectedCodecName != null) {
                    Log.i("MediaCodecDecoder", "findDecoderForFormat selected: $selectedCodecName")
                }
            }
        }

        val newCodec = if (selectedCodecName != null) {
            MediaCodec.createByCodecName(selectedCodecName)
        } else {
            Log.w("MediaCodecDecoder", "Falling back to default AVC decoder")
            MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        }
        newCodec.setCallback(object : MediaCodec.Callback() {
            override fun onInputBufferAvailable(mediaCodec: MediaCodec, inputBufferIndex: Int) {
                inputBufferIndexChannel.trySend(inputBufferIndex)
            }
            override fun onOutputBufferAvailable(
                mediaCodec: MediaCodec, outputBufferIndex: Int, bufferInformation: MediaCodec.BufferInfo
            ) {
                val capturedPtsUs: Long = bufferInformation.presentationTimeUs
                if (isFrameCountLogEnabled) {
                    Log.d(
                        "FRAMECOUNT",
                        "stage=dec ptsUs=$capturedPtsUs wallMs=${System.currentTimeMillis()}"
                    )
                }
                if (surface != null) {
                    mediaCodec.releaseOutputBuffer(outputBufferIndex, System.nanoTime())
                } else {
                    mediaCodec.releaseOutputBuffer(outputBufferIndex, false)
                }
                // Approximate the frame's actual on-screen present time by hopping
                // to the main looper and posting a Choreographer frame callback;
                // it fires at the next vsync, which is when SurfaceFlinger
                // typically latches our just-queued buffer. Wall ms at fire is a
                // lower-bound estimate of when the frame hit the panel.
                if (isFrameCountLogEnabled) {
                    mainHandler.post {
                        Choreographer.getInstance().postFrameCallback {
                            Log.d(
                                "FRAMECOUNT",
                                "stage=present ptsUs=$capturedPtsUs wallMs=${System.currentTimeMillis()}"
                            )
                        }
                    }
                }
                frameSink.onFrameRendered(capturedPtsUs)
            }
            override fun onError(mediaCodec: MediaCodec, exception: MediaCodec.CodecException) {
                Log.e("MediaCodecDecoder", "onError: ${exception.diagnosticInfo} errorCode=${exception.errorCode} isRecoverable=${exception.isRecoverable} isTransient=${exception.isTransient}")
                // Async codec failure — the activity's synchronous try/catch around decoder.start()
                // cannot observe this path, so we emit defense-in-depth here.
                Diagnostics.report(PipelineEvent.DecoderFailed(sid = streamId, cause = exception))
            }
            override fun onOutputFormatChanged(mediaCodec: MediaCodec, newFormat: MediaFormat) {
                Log.d("MediaCodecDecoder", "onOutputFormatChanged: $newFormat")
            }
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
                if (isFrameCountLogEnabled) {
                    Log.d(
                        "FRAMECOUNT",
                        "stage=reasm ptsUs=${encodedFrame.presentationTimestampMicroseconds} wallMs=${System.currentTimeMillis()}"
                    )
                }
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
        if (outputToSurface) frameSink.releaseSurface()
    }

    companion object {
        private val isFrameCountLogEnabled = Log.isLoggable("FRAMECOUNT", Log.DEBUG)
    }
}
