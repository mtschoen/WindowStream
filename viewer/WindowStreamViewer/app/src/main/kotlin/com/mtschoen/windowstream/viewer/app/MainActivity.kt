package com.mtschoen.windowstream.viewer.app

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CircularProgressIndicator
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
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.control.MultiStreamControlClient
import com.mtschoen.windowstream.viewer.control.MultiStreamControlConnection
import com.mtschoen.windowstream.viewer.control.StreamLifecycleEvent
import com.mtschoen.windowstream.viewer.control.WindowDescriptor
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.demo.ObservabilityOverlay
import com.mtschoen.windowstream.viewer.discovery.NetworkServiceDiscoveryClient
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.observability.Diagnostics
import com.mtschoen.windowstream.viewer.observability.PipelineEvent
import com.mtschoen.windowstream.viewer.observability.ViewerStateReducer
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.StreamMultiplexer
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import com.mtschoen.windowstream.viewer.xr.SpatialWindowManager
import com.mtschoen.windowstream.viewer.xr.SpatialWindowManagerScene
import com.mtschoen.windowstream.viewer.xr.XrPanelSink
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import java.net.InetAddress
import java.util.concurrent.ConcurrentHashMap

/**
 * Galaxy XR (gxr-flavor) launcher activity — a spatial window manager.
 *
 * Flow:
 * 1. Auto-discover an mDNS-advertised server (or use --es streamHost / --ei streamPort).
 * 2. Render [SpatialWindowManagerScene]: a persistent drawer listing the server's
 *    window catalogue plus one movable spatial panel per open window, each with a
 *    chrome bar (close / minimize / resize).
 * 3. Tapping "Open" in the drawer spawns a window as its own [SpatialExternalSurface]
 *    panel via [MultiStreamControlConnection.openStream]; multiple panels stream
 *    concurrently. Close tears the stream down; minimize pauses it (and shrinks the
 *    panel); resize scales the panel.
 *
 * The [SpatialWindowManager] holds the pure panel state; this Activity owns each
 * window's runtime resources ([WindowRuntime]). Resize is viewer-side panel
 * scaling, not source-window resizing (which would need new protocol messages).
 */
class MainActivity : ComponentActivity() {

    private sealed class UiState {
        data object Connecting : UiState()
        data object Connected : UiState()
        data class Failed(val message: String) : UiState()
    }

    private data class ResolvedServer(
        val host: String,
        val port: Int,
        val label: String,
        val source: ServerInformation?,
    )

    /** Per-window runtime resources, parallel to the manager's pure panel state. */
    private class WindowRuntime(
        val sink: XrPanelSink,
        val pipelineScope: CoroutineScope,
    ) {
        @Volatile var streamId: Int? = null
        @Volatile var decoder: MediaCodecDecoder? = null
    }

    private var uiState by mutableStateOf<UiState>(UiState.Connecting)
    private var statusBanner by mutableStateOf<String?>(null)
    private var connection: MultiStreamControlConnection? = null
    private var lowLatencyWifiLock: WifiManager.WifiLock? = null
    private lateinit var observabilityOverlay: ObservabilityOverlay

    private val windowManager = SpatialWindowManager()
    private val windowCatalogue = MutableStateFlow<Map<ULong, WindowDescriptor>>(emptyMap())
    private val runtimes = ConcurrentHashMap<ULong, WindowRuntime>()

    // The v2 protocol multiplexes EVERY stream onto the single viewer UDP endpoint
    // announced once via VIEWER_READY (see ViewerReadyMessage on the server). So we
    // bind ONE receiver for the whole connection and fan frames out to per-window
    // decoders by streamId. Binding a socket per window instead made each new
    // VIEWER_READY overwrite the server's single endpoint, starving every stream but
    // the last one opened (black panels).
    private val multiplexer = StreamMultiplexer()
    private var sharedUdpReceiver: UdpTransportReceiver? = null

    // Pipeline coroutines must NOT run on Main: the scene's onSurfaceCreated fires
    // on Main and calls XrPanelSink.provideSurfaceFromXrSystem, while the decoder
    // (on IO) blocks in acquireSurface until that provide happens. Running the
    // openStream/decoder work on IO keeps Main free to recompose + provide.
    private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        acquireWifiLock()

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
                        UiState.Connected -> WindowManagerScene()
                        is UiState.Failed -> FailedScreen(state.message)
                    }
                    // GONE by default, so it does not occlude the spatial panels;
                    // the ℹ button toggles it for diagnostics.
                    AndroidView(
                        factory = { observabilityOverlay.rootView },
                        modifier = Modifier.fillMaxSize(),
                    )
                    TextButton(
                        onClick = { observabilityOverlay.toggle() },
                        modifier = Modifier.align(Alignment.TopEnd).padding(8.dp),
                    ) {
                        Text("ℹ", fontSize = 18.sp)
                    }
                    statusBanner?.let { banner ->
                        Surface(
                            color = MaterialTheme.colorScheme.errorContainer,
                            modifier = Modifier.align(Alignment.TopCenter).padding(8.dp),
                        ) {
                            Text(
                                text = banner,
                                color = MaterialTheme.colorScheme.onErrorContainer,
                                fontSize = 14.sp,
                                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                            )
                        }
                    }
                }
            }
        }

        activityScope.launch { startupConnect() }
    }

    // ─── Compose screens ─────────────────────────────────────────────────────

    @Composable
    private fun ConnectingScreen() {
        Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(
                modifier = Modifier.fillMaxSize().padding(32.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                CircularProgressIndicator()
                Spacer16()
                Text(
                    text = "Searching for WindowStream server…",
                    fontSize = 18.sp,
                    color = MaterialTheme.colorScheme.onBackground,
                )
            }
        }
    }

    @Composable
    private fun FailedScreen(message: String) {
        Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(
                modifier = Modifier.fillMaxSize().padding(32.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text(
                    text = "Connection failed",
                    fontSize = 24.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onBackground,
                )
                Spacer16()
                Text(
                    text = message,
                    fontSize = 16.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }

    @Composable
    private fun Spacer16() {
        Column(modifier = Modifier.height(16.dp)) {}
    }

    @Composable
    private fun WindowManagerScene() {
        val catalogue by windowCatalogue.collectAsState()
        val panels by windowManager.panels.collectAsState()
        SpatialWindowManagerScene(
            windowCatalogue = catalogue,
            panels = panels,
            onSpawn = { windowId -> spawnWindow(windowId) },
            onClose = { windowId -> closeWindow(windowId) },
            onToggleMinimize = { windowId -> toggleMinimize(windowId) },
            onAdjustScale = { windowId, delta -> windowManager.adjustScale(windowId, delta) },
            onSurfaceProvided = { windowId, surface ->
                runtimes[windowId]?.sink?.provideSurfaceFromXrSystem(surface)
            },
            onSurfaceDestroyed = { windowId ->
                runtimes[windowId]?.sink?.releaseSurface()
            },
        )
    }

    // ─── Connection ──────────────────────────────────────────────────────────

    private suspend fun startupConnect() {
        try {
            val resolved: ResolvedServer = resolveServer()
            Log.i(TAG, "connecting to ${resolved.label} at ${resolved.host}:${resolved.port}")

            val client = MultiStreamControlClient(
                host = resolved.host,
                port = resolved.port,
                displayCapabilities = DisplayCapabilities(
                    maximumWidth = 3840,
                    maximumHeight = 2160,
                    supportedCodecs = listOf("h264"),
                ),
            )
            val connectStartNanos: Long = System.nanoTime()
            val liveConnection: MultiStreamControlConnection = client.connect(activityScope)
            val connectDurationMs: Long = (System.nanoTime() - connectStartNanos) / 1_000_000L
            connection = liveConnection
            Diagnostics.report(PipelineEvent.TcpConnected(durationMs = connectDurationMs))

            windowCatalogue.value = liveConnection.serverHello.windows.associateBy { it.windowId }
            Diagnostics.report(
                PipelineEvent.ServerHelloReceived(
                    liveConnection.serverHello.windows.size,
                    liveConnection.serverHello.udpPort,
                ),
            )
            startSharedTransport(liveConnection)
            activityScope.launch { collectWindowEvents(liveConnection) }

            uiState = UiState.Connected

            // Optional adb auto-spawn: --ela selectedWindowHwnds <hwnd,…>.
            val targetHwnds: LongArray = intent.getLongArrayExtra("selectedWindowHwnds") ?: LongArray(0)
            for (hwnd in targetHwnds) {
                liveConnection.serverHello.windows.firstOrNull { it.hwnd == hwnd }?.let { window ->
                    spawnWindow(window.windowId)
                }
            }
        } catch (throwable: Throwable) {
            Log.e(TAG, "startup connect failed", throwable)
            uiState = UiState.Failed(throwable.message ?: throwable.javaClass.simpleName)
        }
    }

    private suspend fun resolveServer(): ResolvedServer {
        val intentHost: String? = intent.getStringExtra("streamHost")
        val intentPort: Int = intent.getIntExtra("streamPort", -1)
        if (intentHost != null && intentPort > 0) {
            return ResolvedServer(host = intentHost, port = intentPort, label = intentHost, source = null)
        }
        val discoveryClient = NetworkServiceDiscoveryClient(applicationContext)
        Diagnostics.report(PipelineEvent.DiscoveryStarted)
        val discovered: ServerInformation = try {
            withTimeout(30_000) { discoveryClient.discover(activityScope).first() }
        } catch (timeout: TimeoutCancellationException) {
            Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
            throw timeout
        }
        Diagnostics.report(
            PipelineEvent.DiscoveryResultReceived(
                hostname = discovered.hostname,
                address = discovered.host.hostAddress ?: discovered.hostname,
                port = discovered.controlPort,
            ),
        )
        return ResolvedServer(
            host = discovered.host.hostAddress ?: discovered.hostname,
            port = discovered.controlPort,
            label = discovered.hostname,
            source = discovered,
        )
    }

    private suspend fun collectWindowEvents(liveConnection: MultiStreamControlConnection) {
        activityScope.launch {
            liveConnection.incoming.filterIsInstance<ControlMessage.WindowAdded>().collect { event ->
                windowCatalogue.value = windowCatalogue.value + (event.window.windowId to event.window)
            }
        }
        activityScope.launch {
            liveConnection.incoming.filterIsInstance<ControlMessage.WindowRemoved>().collect { event ->
                windowCatalogue.value = windowCatalogue.value - event.windowId
            }
        }
        activityScope.launch {
            liveConnection.incoming.filterIsInstance<ControlMessage.WindowUpdated>().collect { event ->
                val existing: WindowDescriptor = windowCatalogue.value[event.windowId] ?: return@collect
                val updated: WindowDescriptor = existing.copy(
                    title = event.title ?: existing.title,
                    physicalWidth = event.physicalWidth ?: existing.physicalWidth,
                    physicalHeight = event.physicalHeight ?: existing.physicalHeight,
                )
                windowCatalogue.value = windowCatalogue.value + (event.windowId to updated)
                if (event.physicalWidth != null && event.physicalHeight != null) {
                    windowManager.updateDimensions(event.windowId, event.physicalWidth!!, event.physicalHeight!!)
                }
            }
        }
    }

    // ─── Spawn / close / minimize ──────────────────────────────────────────────

    private fun spawnWindow(windowId: ULong) {
        if (runtimes.containsKey(windowId)) return
        val sink = XrPanelSink()
        val pipelineScope = CoroutineScope(SupervisorJob(activityScope.coroutineContext.job) + Dispatchers.IO)
        runtimes[windowId] = WindowRuntime(sink = sink, pipelineScope = pipelineScope)
        statusBanner = null
        activityScope.launch { runStreamPipeline(windowId) }
    }

    private suspend fun runStreamPipeline(windowId: ULong) {
        val liveConnection = connection ?: return
        val runtime = runtimes[windowId] ?: return
        Diagnostics.report(PipelineEvent.OpenStreamSent(windowId))
        liveConnection.openStream(windowId, activityScope).collect { event ->
            when (event) {
                is StreamLifecycleEvent.Opened -> {
                    Log.i(TAG, "stream opened: streamId=${event.streamId} ${event.width}x${event.height}")
                    Diagnostics.report(PipelineEvent.StreamOpened(event.streamId, event.width, event.height))
                    runtime.streamId = event.streamId
                    val title: String = windowCatalogue.value[windowId]?.let { it.title.ifBlank { it.processName } }
                        ?: "Window $windowId"
                    runOnUiThread {
                        windowManager.addPanel(
                            windowId = windowId,
                            streamId = event.streamId,
                            title = title,
                            contentWidthPixels = event.width,
                            contentHeightPixels = event.height,
                        )
                    }
                    startDecoder(windowId, event)
                }
                is StreamLifecycleEvent.Refused -> {
                    Log.e(TAG, "stream refused: ${event.errorCode} — ${event.message}")
                    Diagnostics.report(PipelineEvent.StreamRefused(0, event.errorCode, event.message))
                    statusBanner = "Couldn't open that window: ${event.message}"
                    teardownRuntime(windowId)
                }
                is StreamLifecycleEvent.Stopped -> {
                    Log.i(TAG, "stream stopped: ${event.reason.reason}")
                    Diagnostics.report(PipelineEvent.StreamStopped(runtime.streamId ?: 0, event.reason.reason.name))
                    runOnUiThread { windowManager.removePanel(windowId) }
                    teardownRuntime(windowId)
                }
                is StreamLifecycleEvent.Stalled -> {
                    // A stall is not a stop: keep the panel and decoder mounted; surface the
                    // stall state so the per-panel overlay can show "Source not rendering".
                    Log.i(TAG, "source stalled: streamId=${event.streamId} cause=${event.cause}")
                    Diagnostics.report(PipelineEvent.SourceStalled(event.streamId, event.cause))
                    runOnUiThread { windowManager.setStalled(event.streamId, true, event.cause) }
                }
                is StreamLifecycleEvent.Resumed -> {
                    Log.i(TAG, "source resumed: streamId=${event.streamId}")
                    Diagnostics.report(PipelineEvent.SourceResumed(event.streamId))
                    runOnUiThread { windowManager.setStalled(event.streamId, false, null) }
                }
            }
        }
    }

    /**
     * Binds the single UDP receiver for this connection and announces its port via
     * VIEWER_READY exactly once, then pumps every reassembled frame into the
     * [StreamMultiplexer] which fans them out to per-window decoders by streamId.
     */
    private suspend fun startSharedTransport(liveConnection: MultiStreamControlConnection) {
        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0,
            emissionCapacity = MULTIPLEX_EMISSION_CAPACITY,
        )
        sharedUdpReceiver = receiver
        val frames: Flow<EncodedFrame> = receiver.start(activityScope)
        Diagnostics.report(PipelineEvent.UdpBound(receiver.boundPort))
        liveConnection.send(ControlMessage.ViewerReady(viewerUdpPort = receiver.boundPort))
        activityScope.launch {
            frames.collect { frame -> multiplexer.route(frame) }
        }
    }

    private suspend fun startDecoder(windowId: ULong, opened: StreamLifecycleEvent.Opened) {
        val liveConnection = connection ?: return
        val runtime = runtimes[windowId] ?: return

        // The shared receiver is already bound and VIEWER_READY already sent; this
        // stream's frames arrive multiplexed and are demultiplexed here by streamId.
        val frames: Flow<EncodedFrame> = multiplexer.flowFor(opened.streamId)
        liveConnection.requestKeyframe(opened.streamId)

        val decoder = MediaCodecDecoder(
            frameSink = runtime.sink,
            onKeyframeRequested = { runCatching { liveConnection.requestKeyframe(opened.streamId) } },
            streamId = opened.streamId,
        )
        runtime.decoder = decoder
        Diagnostics.report(PipelineEvent.DecoderStarting(opened.streamId, opened.width, opened.height))
        decoder.start(runtime.pipelineScope, frames, opened.width, opened.height)
        Diagnostics.report(PipelineEvent.DecoderStarted(opened.streamId))
    }

    private fun closeWindow(windowId: ULong) {
        val runtime = runtimes[windowId] ?: return
        val streamId: Int? = runtime.streamId
        runOnUiThread { windowManager.removePanel(windowId) }
        activityScope.launch {
            if (streamId != null) {
                runCatching { connection?.closeStream(streamId) }
            }
            teardownRuntime(windowId)
        }
    }

    private fun toggleMinimize(windowId: ULong) {
        val nowMinimized: Boolean = windowManager.toggleMinimize(windowId) ?: return
        val streamId: Int = runtimes[windowId]?.streamId ?: return
        activityScope.launch {
            if (nowMinimized) {
                runCatching { connection?.pauseStream(streamId) }
            } else {
                runCatching { connection?.resumeStream(streamId) }
            }
        }
    }

    private suspend fun teardownRuntime(windowId: ULong) {
        val runtime = runtimes.remove(windowId) ?: return
        runtime.streamId?.let { multiplexer.closeStream(it) }
        runtime.decoder?.runCatching { stop() }
        runCatching { runtime.pipelineScope.coroutineContext.job.cancelAndJoin() }
    }

    // ─── Wifi lock + lifecycle ─────────────────────────────────────────────────

    private fun acquireWifiLock() {
        val wifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val lock = wifiManager.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "XrWindowManager")
        lock.acquire()
        lowLatencyWifiLock = lock
        Diagnostics.report(PipelineEvent.WifiLockAcquired)
    }

    override fun onDestroy() {
        lowLatencyWifiLock?.runCatching {
            if (isHeld) {
                release()
                Diagnostics.report(PipelineEvent.WifiLockReleased)
            }
        }
        lowLatencyWifiLock = null
        runtimes.values.forEach { it.decoder?.runCatching { stop() } }
        runtimes.clear()
        sharedUdpReceiver?.runCatching { close() }
        sharedUdpReceiver = null
        val liveConnection: MultiStreamControlConnection? = connection
        connection = null
        if (liveConnection != null) {
            // Detached scope: activityScope is about to be cancelled but close()
            // must finish flushing the socket teardown.
            CoroutineScope(SupervisorJob() + Dispatchers.IO).launch {
                runCatching { liveConnection.close() }
            }
        }
        activityScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "XrMain"

        // The shared receiver feeds frames for all streams through one emission
        // channel before demux; a generous buffer keeps one stream's frames from
        // being dropped by another stream's arrival under concurrent load.
        const val MULTIPLEX_EMISSION_CAPACITY = 256
    }
}
