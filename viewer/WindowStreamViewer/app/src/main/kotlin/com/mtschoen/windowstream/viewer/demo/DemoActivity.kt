package com.mtschoen.windowstream.viewer.demo

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import com.mtschoen.windowstream.viewer.control.ActiveStreamDescriptor
import com.mtschoen.windowstream.viewer.control.ControlClient
import com.mtschoen.windowstream.viewer.control.ControlConnection
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import java.net.InetAddress

/**
 * Phone-compatible demo activity that bypasses Jetpack XR. Connects directly to
 * a server via intent extras and renders decoded frames onto a plain SurfaceView.
 *
 * Usage from a development machine:
 *   adb shell am start \
 *     -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
 *     --es streamHost 10.0.2.2 \
 *     --ei streamPort 64000
 *
 * The magic IP 10.0.2.2 is how an Android emulator reaches the host's loopback.
 * For a physical device on the same LAN, pass the host's real IP.
 *
 * Surface lifecycle: a window resize destroys and recreates the SurfaceView's
 * Surface. Each pipeline (ControlClient + UdpTransportReceiver + MediaCodec-
 * Decoder) is launched in its own CoroutineScope parented to [demoScope].
 * On surfaceDestroyed the pipeline scope is cancelled AND joined so every
 * worker coroutine — including MediaCodec's native output callbacks — has
 * finished before a new pipeline is permitted to start on the new Surface.
 * [pipelineLock] serializes the callbacks so create/destroy events can't
 * interleave.
 */
class DemoActivity : Activity() {
    private val demoScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val pipelineLock: Mutex = Mutex()
    private var pipelineScope: CoroutineScope? = null
    private var activeDecoder: MediaCodecDecoder? = null
    @Volatile private var controlConnection: ControlConnection? = null

    private lateinit var streamHost: String
    private var streamPort: Int = -1

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        streamHost = intent.getStringExtra("streamHost")
            ?: error("DemoActivity requires --es streamHost <address>")
        streamPort = intent.getIntExtra("streamPort", -1)
        require(streamPort > 0) { "DemoActivity requires --ei streamPort <port>" }

        Log.i(TAG, "connecting to $streamHost:$streamPort")

        val surfaceView = SurfaceView(this)
        setContentView(surfaceView)

        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                val surface: Surface = holder.surface
                demoScope.launch {
                    pipelineLock.withLock {
                        // Defensive: in case onResume created without a prior destroy,
                        // or any residual state exists from a prior attempt.
                        tearDownPipelineLocked()
                        startPipelineLocked(surface)
                    }
                }
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
                // Size changes come through as destroy → create. No-op here.
            }

            override fun surfaceDestroyed(holder: SurfaceHolder) {
                demoScope.launch {
                    pipelineLock.withLock {
                        tearDownPipelineLocked()
                    }
                }
            }
        })
    }

    private suspend fun startPipelineLocked(surface: Surface) {
        val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        pipelineScope = scope
        runCatching { runPipeline(scope, DirectSurfaceFrameSink(surface)) }
            .onFailure { throwable -> Log.e(TAG, "pipeline failed to start", throwable) }
    }

    private suspend fun runPipeline(scope: CoroutineScope, frameSink: DirectSurfaceFrameSink) {
        val client = ControlClient(
            host = streamHost,
            port = streamPort,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 3840,
                maximumHeight = 2160,
                supportedCodecs = listOf("h264")
            )
        )
        val connection: ControlConnection = client.connect(scope)
        controlConnection = connection

        val serverHello: ControlMessage.ServerHello = withTimeout(10_000) {
            connection.incoming.filterIsInstance<ControlMessage.ServerHello>().first()
        }
        val stream: ActiveStreamDescriptor = serverHello.activeStream
            ?: error("server did not report an active stream")

        Log.i(TAG, "stream ${stream.streamId}: ${stream.width}x${stream.height} @ ${stream.framesPerSecond} fps, udp=${stream.udpPort}")

        val udpReceiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0
        )
        val frames: Flow<EncodedFrame> = udpReceiver.start(scope)
        val viewerUdpPort: Int = udpReceiver.boundPort
        Log.i(TAG, "viewer UDP bound on port $viewerUdpPort")

        connection.send(ControlMessage.ViewerReady(streamId = stream.streamId, viewerUdpPort = viewerUdpPort))
        connection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))

        val decoder = MediaCodecDecoder(
            frameSink = frameSink,
            onKeyframeRequested = {
                connection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))
            }
        )
        activeDecoder = decoder
        decoder.start(scope, frames, stream.width, stream.height)
        // No delay(Long.MAX_VALUE). The spawned worker coroutines in `scope`
        // (ControlConnection read loop, UdpTransportReceiver, decoder input/
        // output pumps) keep the scope alive until tearDownPipelineLocked
        // cancels it.
    }

    private suspend fun tearDownPipelineLocked() {
        // Release MediaCodec native resources first so its output callback
        // can't fire on the now-destroyed Surface while we await scope
        // cancellation.
        activeDecoder?.runCatching { stop() }
        activeDecoder = null
        controlConnection = null

        val scope: CoroutineScope = pipelineScope ?: return
        pipelineScope = null
        val job: Job = scope.coroutineContext.job
        runCatching { job.cancelAndJoin() }
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        val isDown: Boolean = when (event.action) {
            android.view.KeyEvent.ACTION_DOWN -> true
            android.view.KeyEvent.ACTION_UP -> false
            else -> return super.dispatchKeyEvent(event)
        }
        val controlMessage: ControlMessage = translateToControlMessage(event, isDown)
            ?: return super.dispatchKeyEvent(event)
        demoScope.launch {
            runCatching { controlConnection?.send(controlMessage) }
        }
        return true
    }

    private fun translateToControlMessage(
        event: android.view.KeyEvent,
        isDown: Boolean
    ): ControlMessage? {
        // Prefer Unicode for printable characters so we don't need a keycode table.
        val unicode: Int = event.unicodeChar
        if (unicode != 0) {
            return ControlMessage.KeyEvent(
                keyCode = unicode,
                isUnicode = true,
                isDown = isDown
            )
        }
        // Fall back to Windows virtual-key codes for control keys.
        val windowsVirtualKey: Int = when (event.keyCode) {
            android.view.KeyEvent.KEYCODE_ENTER -> 0x0D
            android.view.KeyEvent.KEYCODE_DEL -> 0x08       // Backspace
            android.view.KeyEvent.KEYCODE_FORWARD_DEL -> 0x2E
            android.view.KeyEvent.KEYCODE_TAB -> 0x09
            android.view.KeyEvent.KEYCODE_ESCAPE -> 0x1B
            android.view.KeyEvent.KEYCODE_DPAD_LEFT -> 0x25
            android.view.KeyEvent.KEYCODE_DPAD_UP -> 0x26
            android.view.KeyEvent.KEYCODE_DPAD_RIGHT -> 0x27
            android.view.KeyEvent.KEYCODE_DPAD_DOWN -> 0x28
            android.view.KeyEvent.KEYCODE_HOME -> 0x24
            android.view.KeyEvent.KEYCODE_MOVE_END -> 0x23
            else -> return null
        }
        return ControlMessage.KeyEvent(
            keyCode = windowsVirtualKey,
            isUnicode = false,
            isDown = isDown
        )
    }

    override fun onDestroy() {
        // Best-effort synchronous release of MediaCodec natives; remaining
        // teardown cascades via demoScope cancellation.
        activeDecoder?.runCatching { stop() }
        activeDecoder = null
        controlConnection = null
        demoScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "WindowStreamDemo"
    }
}
