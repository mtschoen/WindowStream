package com.mtschoen.windowstream.viewer.demo

import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.unit.dp
import androidx.lifecycle.lifecycleScope
import androidx.xr.compose.spatial.Subspace
import androidx.xr.compose.subspace.SpatialExternalSurface
import androidx.xr.compose.subspace.StereoMode
import androidx.xr.compose.subspace.layout.SubspaceModifier
import androidx.xr.compose.subspace.layout.height
import androidx.xr.compose.subspace.layout.movable
import androidx.xr.compose.subspace.layout.offset
import androidx.xr.compose.subspace.layout.width
import com.mtschoen.windowstream.viewer.control.ControlClient
import com.mtschoen.windowstream.viewer.control.ControlConnection
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.control.awaitOrError
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import com.mtschoen.windowstream.viewer.xr.PanelPlacement
import com.mtschoen.windowstream.viewer.xr.XrPanelSink
import com.mtschoen.windowstream.viewer.xr.computePanelDimensionsMeters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import java.net.InetAddress

/**
 * Minimal XR-compositor demo activity. Runs the same v2 protocol handshake
 * as [DemoActivity] but renders decoded frames through [SpatialExternalSurface]
 * (the XR compositor path) instead of [android.view.SurfaceView] (SurfaceFlinger).
 *
 * Launch via adb:
 * ```
 * adb shell am start -n com.mtschoen.windowstream.viewer/.demo.XrDemoActivity \
 *     --es streamHost <ip> --ei streamPort <port>
 * ```
 *
 * Optional: `--ela selectedWindowHwnds <hwnd>` to target a specific window.
 */
class XrDemoActivity : ComponentActivity() {

    companion object {
        private const val TAG = "XrDemo"
    }

    private val xrPanelSink = XrPanelSink()
    private var decoder: MediaCodecDecoder? = null
    private var connection: ControlConnection? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val host: String = intent.getStringExtra("streamHost")
            ?: error("XrDemoActivity requires --es streamHost <ip>")
        val port: Int = intent.getIntExtra("streamPort", -1)
        require(port > 0) { "XrDemoActivity requires --ei streamPort <port>" }
        val selectedWindowHwnds: LongArray = intent.getLongArrayExtra("selectedWindowHwnds")
            ?: LongArray(0)

        Log.i(TAG, "starting XR compositor path: $host:$port")

        // Launch the v2 pipeline on IO
        lifecycleScope.launch(Dispatchers.IO) {
            runPipeline(host, port, selectedWindowHwnds)
        }

        // Compose content: a single SpatialExternalSurface panel
        setContent {
            val dimensions by xrPanelSink.dimensions.collectAsState()
            val (panelWidthMeters, panelHeightMeters) = computePanelDimensionsMeters(dimensions)

            Subspace {
                SpatialExternalSurface(
                    stereoMode = StereoMode.Mono,
                    modifier = SubspaceModifier
                        .width(panelWidthMeters.dp)
                        .height(panelHeightMeters.dp)
                        .offset(z = (-PanelPlacement.DEFAULT_DISTANCE_METERS).dp)
                        .movable()
                ) {
                    onSurfaceCreated { providedSurface ->
                        Log.i(TAG, "SpatialExternalSurface created: $providedSurface")
                        xrPanelSink.provideSurfaceFromXrSystem(providedSurface)
                    }
                    onSurfaceDestroyed {
                        Log.i(TAG, "SpatialExternalSurface destroyed")
                    }
                }
            }
        }
    }

    private suspend fun runPipeline(
        host: String,
        port: Int,
        selectedWindowHwnds: LongArray
    ) {
        val client = ControlClient(
            host = host,
            port = port,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 3840,
                maximumHeight = 2160,
                supportedCodecs = listOf("h264")
            )
        )
        val controlConnection: ControlConnection = client.connect(lifecycleScope)
        connection = controlConnection
        Log.i(TAG, "TCP connected to $host:$port")

        val serverHello: ControlMessage.ServerHello = withTimeout(10_000) {
            controlConnection.incoming.awaitOrError(ControlMessage.ServerHello::class)
        }
        Log.i(TAG, "ServerHello: ${serverHello.windows.size} window(s), udpPort=${serverHello.udpPort}")

        // Pick window: selectedWindowHwnds[0] if present, else first advertised
        val windowId: ULong = if (selectedWindowHwnds.isNotEmpty()) {
            val targetHwnd: Long = selectedWindowHwnds[0]
            serverHello.windows.firstOrNull { it.hwnd == targetHwnd }?.windowId
                ?: error("no window with hwnd=$targetHwnd in ServerHello")
        } else {
            (serverHello.windows.firstOrNull()
                ?: error("server advertised no windows")).windowId
        }
        Log.i(TAG, "opening windowId=$windowId")
        controlConnection.send(ControlMessage.OpenStream(windowId = windowId))

        val stream: ControlMessage.StreamStarted = withTimeout(10_000) {
            controlConnection.incoming.awaitOrError(ControlMessage.StreamStarted::class)
        }
        Log.i(TAG, "StreamStarted: ${stream.width}x${stream.height} streamId=${stream.streamId}")

        val udpReceiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0
        )
        val frames: Flow<EncodedFrame> = udpReceiver.start(lifecycleScope)
        Log.i(TAG, "UDP bound on port ${udpReceiver.boundPort}")

        controlConnection.send(ControlMessage.ViewerReady(viewerUdpPort = udpReceiver.boundPort))
        controlConnection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))

        val newDecoder = MediaCodecDecoder(
            frameSink = xrPanelSink,
            onKeyframeRequested = {
                controlConnection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))
            }
        )
        decoder = newDecoder
        newDecoder.start(lifecycleScope, frames, stream.width, stream.height)
        Log.i(TAG, "decoder started, rendering through XR compositor")
    }

    override fun onDestroy() {
        decoder?.stop()
        connection?.close()
        super.onDestroy()
    }
}
