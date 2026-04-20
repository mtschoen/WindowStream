package com.mtschoen.windowstream.viewer.demo

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.view.SurfaceHolder
import android.view.SurfaceView
import com.mtschoen.windowstream.viewer.control.ActiveStreamDescriptor
import com.mtschoen.windowstream.viewer.control.ControlClient
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
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
 */
class DemoActivity : Activity() {
    private val demoScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var pipelineJob: Job? = null
    @Volatile private var controlConnection: com.mtschoen.windowstream.viewer.control.ControlConnection? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val host = intent.getStringExtra("streamHost")
            ?: error("DemoActivity requires --es streamHost <address>")
        val port = intent.getIntExtra("streamPort", -1)
        require(port > 0) { "DemoActivity requires --ei streamPort <port>" }

        Log.i(TAG, "connecting to $host:$port")

        val surfaceView = SurfaceView(this)
        setContentView(surfaceView)

        surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                startPipeline(host, port, DirectSurfaceFrameSink(holder.surface))
            }
            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
            override fun surfaceDestroyed(holder: SurfaceHolder) {
                pipelineJob?.cancel()
            }
        })
    }

    private fun startPipeline(host: String, port: Int, frameSink: DirectSurfaceFrameSink) {
        pipelineJob = demoScope.launch {
            runCatching { runPipeline(host, port, frameSink) }
                .onFailure { Log.e(TAG, "pipeline failed", it) }
        }
    }

    private suspend fun runPipeline(host: String, port: Int, frameSink: DirectSurfaceFrameSink) {
        val client = ControlClient(
            host = host,
            port = port,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 3840,
                maximumHeight = 2160,
                supportedCodecs = listOf("h264")
            )
        )
        val connection = client.connect(demoScope)
        controlConnection = connection

        val serverHello = withTimeout(10_000) {
            connection.incoming.filterIsInstance<ControlMessage.ServerHello>().first()
        }
        val stream: ActiveStreamDescriptor = serverHello.activeStream
            ?: error("server did not report an active stream")

        Log.i(TAG, "stream ${stream.streamId}: ${stream.width}x${stream.height} @ ${stream.framesPerSecond} fps, udp=${stream.udpPort}")

        // Open UDP receiver on an ephemeral local port; packets sent TO us from the server
        // arrive regardless of what local port we bound. The server sends to the endpoint
        // it learned when we connected the TCP socket (NAT-style).
        val udpReceiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0
        )
        val frames: Flow<com.mtschoen.windowstream.viewer.transport.EncodedFrame> = udpReceiver.start(demoScope)
        val viewerUdpPort = udpReceiver.boundPort
        Log.i(TAG, "viewer UDP bound on port $viewerUdpPort")

        // Announce our UDP port so the server can register our endpoint.
        connection.send(ControlMessage.ViewerReady(streamId = stream.streamId, viewerUdpPort = viewerUdpPort))

        // Request a keyframe so we can start decoding immediately.
        connection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))

        // Feed frames into the decoder which renders onto the SurfaceView.
        val decoder = MediaCodecDecoder(
            frameSink = frameSink,
            onKeyframeRequested = {
                connection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))
            }
        )
        decoder.start(demoScope, frames, stream.width, stream.height)

        // Keep the activity alive; the pipeline runs until the activity is destroyed.
        kotlinx.coroutines.delay(Long.MAX_VALUE)
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        val isDown = when (event.action) {
            android.view.KeyEvent.ACTION_DOWN -> true
            android.view.KeyEvent.ACTION_UP -> false
            else -> return super.dispatchKeyEvent(event)
        }
        val controlMessage = translateToControlMessage(event, isDown) ?: return super.dispatchKeyEvent(event)
        demoScope.launch(kotlinx.coroutines.Dispatchers.IO) {
            runCatching { controlConnection?.send(controlMessage) }
        }
        return true
    }

    private fun translateToControlMessage(
        event: android.view.KeyEvent,
        isDown: Boolean
    ): com.mtschoen.windowstream.viewer.control.ControlMessage? {
        // Prefer Unicode for printable characters so we don't need a keycode table.
        val unicode = event.unicodeChar
        if (unicode != 0) {
            return com.mtschoen.windowstream.viewer.control.ControlMessage.KeyEvent(
                keyCode = unicode,
                isUnicode = true,
                isDown = isDown
            )
        }
        // Fall back to Windows virtual-key codes for control keys.
        val windowsVk = when (event.keyCode) {
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
        return com.mtschoen.windowstream.viewer.control.ControlMessage.KeyEvent(
            keyCode = windowsVk,
            isUnicode = false,
            isDown = isDown
        )
    }

    override fun onDestroy() {
        controlConnection = null
        demoScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "WindowStreamDemo"
    }
}
