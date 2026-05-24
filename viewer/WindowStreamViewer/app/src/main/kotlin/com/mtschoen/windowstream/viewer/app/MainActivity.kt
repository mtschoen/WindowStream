package com.mtschoen.windowstream.viewer.app

import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.xr.compose.spatial.Subspace
import androidx.xr.compose.subspace.SpatialExternalSurface
import androidx.xr.compose.subspace.StereoMode
import androidx.xr.compose.subspace.layout.SubspaceModifier
import androidx.xr.compose.subspace.layout.height
import androidx.xr.compose.subspace.layout.movable
import androidx.xr.compose.subspace.layout.offset
import androidx.xr.compose.subspace.layout.width
import com.mtschoen.windowstream.viewer.app.ui.WindowPickerScreen
import com.mtschoen.windowstream.viewer.app.ui.WindowPickerViewModel
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.demo.ObservabilityOverlay
import com.mtschoen.windowstream.viewer.observability.ViewerStateReducer
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.control.MultiStreamControlClient
import com.mtschoen.windowstream.viewer.control.MultiStreamControlConnection
import com.mtschoen.windowstream.viewer.control.StreamLifecycleEvent
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.discovery.NetworkServiceDiscoveryClient
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.observability.Diagnostics
import com.mtschoen.windowstream.viewer.observability.PipelineEvent
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import com.mtschoen.windowstream.viewer.xr.PanelPlacement
import com.mtschoen.windowstream.viewer.xr.XrPanelSink
import com.mtschoen.windowstream.viewer.xr.computePanelDimensionsMeters
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import java.net.InetAddress

/**
 * Galaxy XR (gxr-flavor) launcher activity.
 *
 * Flow:
 * 1. Auto-discover an mDNS-advertised server (or use --es streamHost / --ei streamPort).
 * 2. Open a [MultiStreamControlConnection] and show [WindowPickerScreen] as a 2D
 *    Compose panel — the same picker the portable flavor uses, with live
 *    WINDOW_ADDED / REMOVED / UPDATED push events.
 * 3. On Connect, tear down the picker and render a single [SpatialExternalSurface]
 *    streaming the selected window through the XR compositor path.
 * 4. A "Pick another window" button on the streaming view closes the current
 *    stream and returns to the picker so the user can re-select without
 *    restarting the activity.
 *
 * Multi-panel (several SpatialExternalSurface panels at once) is a follow-up;
 * this MVP unblocks "I can't pick which window to see" while keeping the same
 * one-stream-at-a-time semantics as the existing XrDemoActivity path.
 */
class MainActivity : ComponentActivity() {

    private sealed class UiState {
        data object Connecting : UiState()
        data class Picking(val viewModel: WindowPickerViewModel) : UiState()
        data class Streaming(val sink: XrPanelSink) : UiState()
        data class Failed(val message: String) : UiState()
    }

    private data class ResolvedServer(
        val host: String,
        val port: Int,
        val label: String,
        val source: ServerInformation?
    )

    private var uiState by mutableStateOf<UiState>(UiState.Connecting)
    private var pickerErrorBanner by mutableStateOf<String?>(null)
    private var resolvedServer: ResolvedServer? = null
    private var connection: MultiStreamControlConnection? = null
    private var pickerScope: CoroutineScope? = null
    private var streamingJob: Job? = null
    private var activeStreamId: Int? = null
    private var activeDecoder: MediaCodecDecoder? = null
    private lateinit var observabilityOverlay: ObservabilityOverlay

    // Pipeline + multi-stream openStream coroutines must NOT run on Main.
    // SpatialExternalSurface.onSurfaceCreated fires on Main and calls
    // XrPanelSink.provideSurfaceFromXrSystem, which blocks Main on a rendezvous
    // channel send until decoder.start() calls acquireSurface. If openStream's
    // mailbox-await coroutine sits on Main waiting for STREAM_STARTED, it
    // deadlocks: the mailbox-await never resumes, decoder never starts,
    // acquireSurface never runs, the rendezvous send never completes.
    // UnifiedStreamingActivity uses the same pattern.
    private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Wire observability overlay + event collector.
        observabilityOverlay = ObservabilityOverlay(this)
        val app = applicationContext as WindowStreamViewerApplication
        val reducer = ViewerStateReducer()
        activityScope.launch {
            app.inAppBufferTree.events.collect { event ->
                event.pipelineEvent?.let { reducer.apply(it) }
                runOnUiThread {
                    observabilityOverlay.appendEvent(event)
                    observabilityOverlay.renderState(reducer.state)
                }
            }
        }

        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                Box(modifier = Modifier.fillMaxSize()) {
                    when (val state = uiState) {
                        UiState.Connecting -> ConnectingScreen()
                        is UiState.Picking -> PickerScreen(state.viewModel)
                        is UiState.Streaming -> StreamingScene(state.sink)
                        is UiState.Failed -> FailedScreen(state.message)
                    }
                    // Observability overlay floats above all screens; toggle with ℹ button.
                    AndroidView(
                        factory = { observabilityOverlay.rootView },
                        modifier = Modifier.fillMaxSize()
                    )
                    TextButton(
                        onClick = { observabilityOverlay.toggle() },
                        modifier = Modifier.align(Alignment.TopEnd).padding(8.dp)
                    ) {
                        Text("ℹ", fontSize = 18.sp)
                    }
                }
            }
        }

        activityScope.launch {
            startupConnect()
        }
    }

    // ─── Compose screens ─────────────────────────────────────────────────────

    @Composable
    private fun ConnectingScreen() {
        Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(
                modifier = Modifier.fillMaxSize().padding(32.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                CircularProgressIndicator()
                Spacer(modifier = Modifier.height(16.dp))
                Text(
                    text = "Searching for WindowStream server…",
                    fontSize = 18.sp,
                    color = MaterialTheme.colorScheme.onBackground
                )
            }
        }
    }

    @Composable
    private fun PickerScreen(viewModel: WindowPickerViewModel) {
        val currentServer: ServerInformation = resolvedServer?.source
            ?: syntheticServerInformation(resolvedServer)
            ?: return
        val banner: String? = pickerErrorBanner
        Column(modifier = Modifier.fillMaxSize()) {
            if (banner != null) {
                Surface(
                    color = MaterialTheme.colorScheme.errorContainer,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        text = banner,
                        color = MaterialTheme.colorScheme.onErrorContainer,
                        fontSize = 14.sp,
                        modifier = Modifier.padding(horizontal = 24.dp, vertical = 12.dp)
                    )
                }
            }
            WindowPickerScreen(
                server = currentServer,
                viewModel = viewModel,
                onConnect = { selectedWindowIds ->
                    if (selectedWindowIds.isNotEmpty()) {
                        val firstWindowId: ULong = selectedWindowIds[0].toULong()
                        pickerErrorBanner = null
                        activityScope.launch {
                            beginStreaming(firstWindowId)
                        }
                    }
                }
            )
        }
    }

    private fun syntheticServerInformation(server: ResolvedServer?): ServerInformation? {
        val resolved = server ?: return null
        return ServerInformation(
            hostname = resolved.label,
            host = InetAddress.getByName(resolved.host),
            controlPort = resolved.port,
            protocolMajorVersion = 2,
            protocolMinorRevision = 0
        )
    }

    @Composable
    private fun FailedScreen(message: String) {
        Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(
                modifier = Modifier.fillMaxSize().padding(32.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(
                    text = "Connection failed",
                    fontSize = 24.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onBackground
                )
                Spacer(modifier = Modifier.height(8.dp))
                Text(
                    text = message,
                    fontSize = 16.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }

    @Composable
    private fun StreamingScene(sink: XrPanelSink) {
        val dimensions by sink.dimensions.collectAsState()
        val (panelWidthMeters, panelHeightMeters) = computePanelDimensionsMeters(dimensions)

        // A small 2D overlay panel for the "Pick another window" affordance,
        // rendered alongside the spatial panel as the activity's main 2D panel.
        // We use a transparent Surface so that it doesn't occlude the spatial video panel.
        Surface(color = androidx.compose.ui.graphics.Color.Transparent) {
            Box(modifier = Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.TopStart) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    FilledTonalButton(onClick = {
                        activityScope.launch { returnToPicker() }
                    }) {
                        Text("Pick another window", fontSize = 16.sp)
                    }
                    Spacer(modifier = Modifier.padding(horizontal = 8.dp))
                    Text(
                        text = "Streaming via XR compositor",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        fontSize = 14.sp
                    )
                }
            }
        }

        Subspace {
            SpatialExternalSurface(
                stereoMode = StereoMode.Mono,
                modifier = SubspaceModifier
                    .width((panelWidthMeters * 1000f).dp)
                    .height((panelHeightMeters * 1000f).dp)
                    .offset(z = (-PanelPlacement.DEFAULT_DISTANCE_METERS * 1000f).dp)
                    .movable()
            ) {
                onSurfaceCreated { providedSurface ->
                    Log.i(TAG, "SpatialExternalSurface created: $providedSurface")
                    Diagnostics.report(PipelineEvent.SurfaceCreated(panelIndex = 0))
                    sink.provideSurfaceFromXrSystem(providedSurface)
                }
                onSurfaceDestroyed {
                    Log.i(TAG, "SpatialExternalSurface destroyed")
                    Diagnostics.report(PipelineEvent.SurfaceDestroyed(panelIndex = 0, "spatial-lifecycle"))
                }
            }
        }
    }

    // ─── Connection + lifecycle ──────────────────────────────────────────────

    private suspend fun startupConnect() {
        try {
            val resolved: ResolvedServer = resolveServer()
            resolvedServer = resolved
            Log.i(TAG, "connecting to ${resolved.label} at ${resolved.host}:${resolved.port}")

            val client = MultiStreamControlClient(
                host = resolved.host,
                port = resolved.port,
                displayCapabilities = DisplayCapabilities(
                    maximumWidth = 3840,
                    maximumHeight = 2160,
                    supportedCodecs = listOf("h264")
                )
            )
            val connectStartNanos: Long = System.nanoTime()
            val liveConnection: MultiStreamControlConnection = client.connect(activityScope)
            val connectDurationMs: Long = (System.nanoTime() - connectStartNanos) / 1_000_000L
            connection = liveConnection
            Log.i(TAG, "connected: ${liveConnection.serverHello.windows.size} window(s) advertised")
            Diagnostics.report(PipelineEvent.TcpConnected(durationMs = connectDurationMs))
            Diagnostics.report(
                PipelineEvent.ServerHelloReceived(
                    liveConnection.serverHello.windows.size,
                    liveConnection.serverHello.udpPort
                )
            )
            val selectedWindowHwnds: LongArray? = intent.getLongArrayExtra("selectedWindowHwnds")
            val targetHwnd: Long? = selectedWindowHwnds?.firstOrNull() ?: intent.getLongExtra("selectedWindowHwnd", -1L).takeIf { it != -1L }
            if (targetHwnd != null) {
                val targetWindow = liveConnection.serverHello.windows.firstOrNull { it.hwnd == targetHwnd }
                if (targetWindow != null) {
                    Log.i(TAG, "found matching target window for HWND=$targetHwnd, auto-streaming windowId=${targetWindow.windowId}")
                    beginStreaming(targetWindow.windowId)
                    return
                }
            }
            showPicker(liveConnection)
        } catch (throwable: Throwable) {
            Log.e(TAG, "startup connect failed", throwable)
            uiState = UiState.Failed(throwable.message ?: throwable.javaClass.simpleName)
        }
    }

    private suspend fun resolveServer(): ResolvedServer {
        val intentHost: String? = intent.getStringExtra("streamHost")
        val intentPort: Int = intent.getIntExtra("streamPort", -1)
        if (intentHost != null && intentPort > 0) {
            return ResolvedServer(
                host = intentHost,
                port = intentPort,
                label = intentHost,
                source = null
            )
        }
        val discoveryClient = NetworkServiceDiscoveryClient(applicationContext)
        Diagnostics.report(PipelineEvent.DiscoveryStarted)
        val discovered: ServerInformation = try {
            withTimeout(30_000) {
                discoveryClient.discover(activityScope).first()
            }
        } catch (timeout: TimeoutCancellationException) {
            Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
            throw timeout
        }
        Diagnostics.report(
            PipelineEvent.DiscoveryResultReceived(
                hostname = discovered.hostname,
                address = discovered.host.hostAddress ?: discovered.hostname,
                port = discovered.controlPort
            )
        )
        return ResolvedServer(
            host = discovered.host.hostAddress ?: discovered.hostname,
            port = discovered.controlPort,
            label = discovered.hostname,
            source = discovered
        )
    }

    private fun showPicker(liveConnection: MultiStreamControlConnection) {
        // A scope tied to the picker so the WindowPickerViewModel collectors are
        // cancelled when we move out of the Picking state. Parented under
        // activityScope so it dies with the activity.
        val scope = CoroutineScope(SupervisorJob(activityScope.coroutineContext.job) + Dispatchers.IO)
        pickerScope = scope
        val viewModel = WindowPickerViewModel(
            initialWindows = liveConnection.serverHello.windows,
            incomingMessages = liveConnection.incoming,
            scope = scope
        )
        uiState = UiState.Picking(viewModel)
    }

    private suspend fun beginStreaming(windowId: ULong) {
        val liveConnection = connection ?: return
        pickerScope?.cancel()
        pickerScope = null

        val sink = XrPanelSink()
        uiState = UiState.Streaming(sink)

        Diagnostics.report(PipelineEvent.OpenStreamSent(windowId))
        val pipelineJob = activityScope.launch {
            try {
                runPipeline(liveConnection, windowId, sink)
            } catch (throwable: Throwable) {
                Log.e(TAG, "stream pipeline failed", throwable)
                uiState = UiState.Failed(throwable.message ?: throwable.javaClass.simpleName)
            }
        }
        streamingJob = pipelineJob
    }

    private suspend fun runPipeline(
        liveConnection: MultiStreamControlConnection,
        windowId: ULong,
        sink: XrPanelSink
    ) {
        Log.i(TAG, "opening stream for windowId=$windowId")
        val openFlow: Flow<StreamLifecycleEvent> = liveConnection.openStream(windowId, activityScope)

        var opened: StreamLifecycleEvent.Opened? = null
        openFlow.collect { event ->
            when (event) {
                is StreamLifecycleEvent.Opened -> {
                    Log.i(TAG, "stream opened: streamId=${event.streamId} ${event.width}x${event.height}")
                    Diagnostics.report(PipelineEvent.StreamOpened(event.streamId, event.width, event.height))
                    opened = event
                    activeStreamId = event.streamId
                    startDecoderForStream(liveConnection, event, sink)
                }
                is StreamLifecycleEvent.Refused -> {
                    Log.e(TAG, "stream refused: ${event.errorCode} — ${event.message}")
                    Diagnostics.report(PipelineEvent.StreamRefused(0, event.errorCode, event.message))
                    pickerErrorBanner = "Couldn't open that window: ${event.message}"
                    val liveConnection = connection
                    if (liveConnection != null) showPicker(liveConnection)
                }
                is StreamLifecycleEvent.Stopped -> {
                    Log.i(TAG, "stream stopped: ${event.reason.reason}")
                    Diagnostics.report(PipelineEvent.StreamStopped(activeStreamId ?: 0, event.reason.reason.name))
                    activeDecoder?.runCatching { stop() }
                    activeDecoder = null
                    activeStreamId = null
                }
            }
        }
        // Suppress unused — keeps the resolved stream descriptor in scope for
        // future log lines / multi-panel work without changing the control flow.
        opened?.let { Log.i(TAG, "pipeline collector ending for streamId=${it.streamId}") }
    }

    private suspend fun startDecoderForStream(
        liveConnection: MultiStreamControlConnection,
        opened: StreamLifecycleEvent.Opened,
        sink: XrPanelSink
    ) {
        val udpReceiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0
        )
        val frames: Flow<EncodedFrame> = udpReceiver.start(activityScope)
        Log.i(TAG, "UDP bound on port ${udpReceiver.boundPort}")
        Diagnostics.report(PipelineEvent.UdpBound(udpReceiver.boundPort))

        liveConnection.send(ControlMessage.ViewerReady(viewerUdpPort = udpReceiver.boundPort))
        liveConnection.requestKeyframe(opened.streamId)

        val decoder = MediaCodecDecoder(
            frameSink = sink,
            onKeyframeRequested = {
                runCatching { liveConnection.requestKeyframe(opened.streamId) }
            }
        )
        activeDecoder = decoder
        Diagnostics.report(PipelineEvent.DecoderStarting(opened.streamId, opened.width, opened.height))
        decoder.start(activityScope, frames, opened.width, opened.height)
        Log.i(TAG, "decoder started, rendering through XR compositor")
        Diagnostics.report(PipelineEvent.DecoderStarted(opened.streamId))
    }

    private suspend fun returnToPicker() {
        val streamId: Int? = activeStreamId
        val liveConnection: MultiStreamControlConnection? = connection
        activeDecoder?.runCatching { stop() }
        activeDecoder = null
        if (streamId != null && liveConnection != null) {
            runCatching { liveConnection.closeStream(streamId) }
        }
        activeStreamId = null
        streamingJob?.cancel()
        streamingJob = null
        if (liveConnection != null) {
            showPicker(liveConnection)
        }
    }

    override fun onDestroy() {
        activeDecoder?.runCatching { stop() }
        activeDecoder = null
        pickerScope?.cancel()
        pickerScope = null
        streamingJob?.cancel()
        streamingJob = null
        val liveConnection: MultiStreamControlConnection? = connection
        connection = null
        if (liveConnection != null) {
            // Use a detached scope: activityScope is about to be cancelled,
            // and close() needs to finish flushing the socket teardown.
            CoroutineScope(SupervisorJob() + Dispatchers.IO).launch {
                runCatching { liveConnection.close() }
            }
        }
        activityScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "XrMain"
    }
}
