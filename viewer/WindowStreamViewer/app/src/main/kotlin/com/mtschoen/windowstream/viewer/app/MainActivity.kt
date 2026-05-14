package com.mtschoen.windowstream.viewer.app

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
import com.mtschoen.windowstream.viewer.discovery.NetworkServiceDiscoveryClient
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import com.mtschoen.windowstream.viewer.xr.PanelPlacement
import com.mtschoen.windowstream.viewer.xr.XrPanelSink
import com.mtschoen.windowstream.viewer.xr.computePanelDimensionsMeters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import java.net.InetAddress

/**
 * XR compositor launcher activity. Discovers the first mDNS-advertised server,
 * performs the full v2 handshake (ServerHello → OpenStream → StreamStarted),
 * and renders decoded frames through [SpatialExternalSurface] — the XR
 * compositor path that bypasses SurfaceFlinger.
 *
 * This is the GXR flavor's LAUNCHER activity, tapped from the spatial home.
 */
class MainActivity : ComponentActivity() {

    companion object {
        private const val TAG = "XrMain"
    }

    private val xrPanelSink = XrPanelSink()
    private var decoder: MediaCodecDecoder? = null
    private var connection: ControlConnection? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val discoveryClient = NetworkServiceDiscoveryClient(applicationContext)

        // Auto-connect: discover first server, open first window
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                Log.i(TAG, "waiting for mDNS discovery…")
                val server: ServerInformation = withTimeout(30_000) {
                    discoveryClient.discover(this).first()
                }
                Log.i(TAG, "discovered ${server.hostname} at ${server.host.hostAddress}:${server.controlPort}")
                runPipeline(server)
            } catch (throwable: Throwable) {
                Log.e(TAG, "pipeline failed", throwable)
            }
        }

        // Compose content: SpatialExternalSurface panel
        setContent {
            val dimensions by xrPanelSink.dimensions.collectAsState()
            val (panelWidthMeters, panelHeightMeters) = computePanelDimensionsMeters(dimensions)

            Subspace {
                SpatialExternalSurface(
                    stereoMode = StereoMode.Mono,
                    modifier = SubspaceModifier
                        .width(1200.dp)
                        .height(675.dp)
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

    private suspend fun runPipeline(server: ServerInformation) {
        val host: String = server.host.hostAddress ?: server.host.toString()
        val port: Int = server.controlPort

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
        Log.i(TAG, "ServerHello: ${serverHello.windows.size} window(s)")
        serverHello.windows.forEachIndexed { index, descriptor ->
            Log.i(TAG, "  [$index] windowId=${descriptor.windowId} hwnd=${descriptor.hwnd} title=${descriptor.title}")
        }

        // Try each window until one succeeds — some (e.g. Discord) block WGC capture.
        var stream: ControlMessage.StreamStarted? = null
        for (descriptor in serverHello.windows) {
            Log.i(TAG, "trying windowId=${descriptor.windowId} (${descriptor.title})")
            controlConnection.send(ControlMessage.OpenStream(windowId = descriptor.windowId))
            try {
                stream = withTimeout(5_000) {
                    controlConnection.incoming.awaitOrError(ControlMessage.StreamStarted::class)
                }
                Log.i(TAG, "StreamStarted: ${stream.width}x${stream.height} streamId=${stream.streamId}")
                break
            } catch (throwable: Throwable) {
                Log.w(TAG, "windowId=${descriptor.windowId} failed: ${throwable.message}")
            }
        }
        if (stream == null) error("no capturable window found")

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
        Log.i(TAG, "decoder started — rendering through XR compositor")
    }

    override fun onDestroy() {
        decoder?.stop()
        connection?.close()
        super.onDestroy()
    }
}
