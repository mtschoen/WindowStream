package com.mtschoen.windowstream.viewer.demo

import android.app.Activity
import android.content.Context
import android.graphics.Color
import android.net.wifi.WifiManager
import android.os.Bundle
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.util.Log
import android.view.Gravity
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.TextView
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.control.MultiStreamControlClient
import com.mtschoen.windowstream.viewer.control.MultiStreamControlConnection
import com.mtschoen.windowstream.viewer.control.StreamLifecycleEvent
import com.mtschoen.windowstream.viewer.control.WindowDescriptor
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.discovery.NetworkServiceDiscoveryClient
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.observability.Diagnostics
import com.mtschoen.windowstream.viewer.observability.PipelineEvent
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
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
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import java.net.InetAddress

/**
 * Unified streaming activity that combines server discovery, window browsing,
 * and multi-panel streaming into a single activity. Replaces the old
 * ServerSelectionActivity → PanelSwitcherActivity two-step flow.
 *
 * Features:
 * - Auto-discovers servers via mDNS, auto-connects to the first one found
 * - Persistent sliding window drawer ([WindowDrawerOverlay]) for browsing and
 *   opening/closing windows at any time during a session
 * - Tab bar for switching between open streams with close (×) buttons
 * - Single [MultiStreamControlConnection] for the entire session lifecycle
 *
 * Intent extras (all optional — defaults to auto-discovery):
 * - `streamHost: String` + `streamPort: Int` — skip discovery, connect directly
 * - `selectedWindowIds: LongArray` — auto-open these windows after connecting
 */
class UnifiedStreamingActivity : Activity() {

    private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val surfaceLock = Mutex()

    private data class PanelState(
        val windowId: ULong,
        val streamId: Int,
        val surfaceView: SurfaceView,
        val tabView: LinearLayout,
        val decoder: MediaCodecDecoder? = null,
        val pipelineScope: CoroutineScope? = null
    )

    private var connection: MultiStreamControlConnection? = null
    private val panels: MutableList<PanelState> = mutableListOf()
    private var activeIndex: Int = -1
    private var lowLatencyWifiLock: WifiManager.WifiLock? = null

    // Live window catalogue from the server.
    private val windowCatalogue = MutableStateFlow<Map<ULong, WindowDescriptor>>(emptyMap())
    private val openWindowIds = MutableStateFlow<Set<ULong>>(emptySet())

    private lateinit var surfaceContainer: FrameLayout
    private lateinit var tabBar: LinearLayout
    private lateinit var statusLabel: TextView
    private lateinit var drawerOverlay: WindowDrawerOverlay
    private lateinit var softInputEditText: EditText
    private lateinit var inputPreviewTextView: TextView
    private var previousSoftInputLength: Int = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        buildLayout()
        acquireWifiLock()

        val directHost: String? = intent.getStringExtra("streamHost")
        val directPort: Int = intent.getIntExtra("streamPort", -1)

        if (directHost != null && directPort > 0) {
            statusLabel.text = "Connecting to $directHost:$directPort…"
            activityScope.launch { connectToServer(directHost, directPort) }
        } else {
            statusLabel.text = "Searching for servers…"
            activityScope.launch { discoverAndConnect() }
        }
    }

    // ─── Layout ──────────────────────────────────────────────────────────────

    private fun buildLayout() {
        // Tab bar (horizontal scrolling)
        tabBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(Color.rgb(25, 25, 35))
            gravity = Gravity.CENTER_VERTICAL
        }

        // Hamburger / drawer toggle button
        val drawerToggle = TextView(this).apply {
            text = "≡"
            setTextColor(Color.WHITE)
            textSize = 22f
            setPadding(28, 0, 20, 0)
            gravity = Gravity.CENTER
            isClickable = true
            isFocusable = true
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.MATCH_PARENT
            )
            setOnClickListener { drawerOverlay.toggle() }
        }

        // "+" add window button
        val addButton = TextView(this).apply {
            text = "+"
            setTextColor(Color.rgb(130, 180, 255))
            textSize = 22f
            setPadding(20, 0, 28, 0)
            gravity = Gravity.CENTER
            isClickable = true
            isFocusable = true
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.MATCH_PARENT
            )
            setOnClickListener { drawerOverlay.show() }
        }

        val tabBarWithControls = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(Color.rgb(25, 25, 35))
            addView(drawerToggle)
            addView(HorizontalScrollView(context).apply {
                addView(tabBar)
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f)
            })
            addView(addButton)
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                TAB_BAR_HEIGHT_PX,
                Gravity.TOP
            )
        }

        surfaceContainer = FrameLayout(this)

        statusLabel = TextView(this).apply {
            setTextColor(Color.rgb(180, 180, 190))
            textSize = 16f
            gravity = Gravity.CENTER
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
        }

        drawerOverlay = WindowDrawerOverlay(this) { windowId, isCurrentlyOpen ->
            onDrawerWindowTapped(windowId, isCurrentlyOpen)
        }

        softInputEditText = EditText(this).apply {
            alpha = 0f
            isFocusable = true
            isFocusableInTouchMode = true
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            setSingleLine(false)
            layoutParams = FrameLayout.LayoutParams(1, 1, Gravity.START or Gravity.BOTTOM)
            addTextChangedListener(buildSoftInputTextWatcher())
        }

        inputPreviewTextView = TextView(this).apply {
            setBackgroundColor(Color.argb(200, 0, 0, 0))
            setTextColor(Color.WHITE)
            textSize = 22f
            setPadding(32, 20, 32, 20)
            visibility = View.GONE
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP
            ).also { it.topMargin = TAB_BAR_HEIGHT_PX }
        }

        val root = FrameLayout(this).apply {
            fitsSystemWindows = true
            setBackgroundColor(Color.BLACK)
            addView(surfaceContainer, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            ))
            addView(statusLabel)
            addView(tabBarWithControls)
            addView(softInputEditText)
            addView(inputPreviewTextView)
            addView(drawerOverlay.rootView)
            isClickable = true
            setOnClickListener { toggleSoftKeyboard() }
        }
        setContentView(root)
    }

    // ─── Discovery + Connection ──────────────────────────────────────────────

    private suspend fun discoverAndConnect() {
        val discoveryClient = NetworkServiceDiscoveryClient(applicationContext)
        Diagnostics.report(PipelineEvent.DiscoveryStarted)
        try {
            val server: ServerInformation = try {
                withTimeout(30_000) {
                    discoveryClient.discover(activityScope).first()
                }
            } catch (timeoutException: TimeoutCancellationException) {
                Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
                runOnUiThread { statusLabel.text = "Discovery timed out" }
                return
            }
            Diagnostics.report(PipelineEvent.DiscoveryResultReceived(
                hostname = server.hostname,
                address = server.host.hostAddress ?: "?",
                port = server.controlPort))
            runOnUiThread { statusLabel.text = "Connecting to ${server.hostname}…" }
            connectToServer(
                server.host.hostAddress ?: server.hostname,
                server.controlPort
            )
        } catch (throwable: Throwable) {
            val host = ""
            val port = 0
            Diagnostics.report(PipelineEvent.TcpConnectFailed(host = host, port = port, cause = throwable))
            runOnUiThread { statusLabel.text = "Connection failed: ${throwable.message}" }
        }
    }

    private suspend fun connectToServer(host: String, port: Int) {
        val client = MultiStreamControlClient(
            host = host,
            port = port,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 3840,
                maximumHeight = 2160,
                supportedCodecs = listOf("h264")
            )
        )
        val connectStart = System.nanoTime()
        Diagnostics.report(PipelineEvent.TcpConnecting(host = host, port = port))
        val liveConnection: MultiStreamControlConnection = client.connect(activityScope)
        val elapsedMs = (System.nanoTime() - connectStart) / 1_000_000
        Diagnostics.report(PipelineEvent.TcpConnected(durationMs = elapsedMs))
        connection = liveConnection

        // Seed window catalogue from ServerHello.
        val initialCatalogue = liveConnection.serverHello.windows.associateBy { it.windowId }
        windowCatalogue.value = initialCatalogue
        Diagnostics.report(PipelineEvent.ServerHelloReceived(
            windowCount = initialCatalogue.size,
            udpPort = liveConnection.serverHello.udpPort))

        runOnUiThread {
            statusLabel.text = if (initialCatalogue.isEmpty()) "No windows available"
            else "Tap ≡ or + to open a window"
            drawerOverlay.updateCatalogue(initialCatalogue, emptySet())
        }

        // Start collecting live window push events.
        activityScope.launch { collectWindowEvents(liveConnection) }

        // Auto-open windows from intent extras if provided.
        val selectedWindowIds: LongArray = intent.getLongArrayExtra("selectedWindowIds") ?: LongArray(0)
        for (rawId in selectedWindowIds) {
            openWindow(rawId.toULong())
        }
    }

    private suspend fun collectWindowEvents(liveConnection: MultiStreamControlConnection) {
        activityScope.launch {
            liveConnection.incoming.filterIsInstance<ControlMessage.WindowAdded>().collect { event ->
                windowCatalogue.value = windowCatalogue.value + (event.window.windowId to event.window)
                runOnUiThread { drawerOverlay.updateCatalogue(windowCatalogue.value, openWindowIds.value) }
            }
        }
        activityScope.launch {
            liveConnection.incoming.filterIsInstance<ControlMessage.WindowRemoved>().collect { event ->
                windowCatalogue.value = windowCatalogue.value - event.windowId
                runOnUiThread { drawerOverlay.updateCatalogue(windowCatalogue.value, openWindowIds.value) }
            }
        }
        activityScope.launch {
            liveConnection.incoming.filterIsInstance<ControlMessage.WindowUpdated>().collect { event ->
                val existing = windowCatalogue.value[event.windowId] ?: return@collect
                val updated = existing.copy(
                    title = event.title ?: existing.title,
                    physicalWidth = event.physicalWidth ?: existing.physicalWidth,
                    physicalHeight = event.physicalHeight ?: existing.physicalHeight
                )
                windowCatalogue.value = windowCatalogue.value + (event.windowId to updated)
                runOnUiThread { drawerOverlay.updateCatalogue(windowCatalogue.value, openWindowIds.value) }
            }
        }
    }

    // ─── Window open / close ─────────────────────────────────────────────────

    private fun onDrawerWindowTapped(windowId: ULong, isCurrentlyOpen: Boolean) {
        activityScope.launch {
            if (isCurrentlyOpen) {
                closeWindow(windowId)
            } else {
                openWindow(windowId)
            }
        }
    }

    private suspend fun openWindow(windowId: ULong) {
        val liveConnection = connection ?: return
        Diagnostics.report(PipelineEvent.OpenStreamSent(windowId = windowId))

        liveConnection.openStream(windowId, activityScope).collect { event ->
            when (event) {
                is StreamLifecycleEvent.Opened -> {
                    Diagnostics.report(PipelineEvent.StreamOpened(event.streamId, event.width, event.height))
                    runOnUiThread {
                        addPanel(windowId, event.streamId, event.width, event.height)
                        openWindowIds.value = openWindowIds.value + windowId
                        drawerOverlay.updateCatalogue(windowCatalogue.value, openWindowIds.value)
                        statusLabel.visibility = View.GONE
                    }
                }
                is StreamLifecycleEvent.Refused -> {
                    Diagnostics.report(PipelineEvent.StreamRefused(0, event.errorCode, event.message))
                    runOnUiThread {
                        statusLabel.text = "Stream refused: ${event.message}"
                        statusLabel.visibility = View.VISIBLE
                    }
                }
                is StreamLifecycleEvent.Stopped -> {
                    Diagnostics.report(PipelineEvent.StreamStopped(event.reason.streamId, event.reason.reason.name))
                    runOnUiThread {
                        removePanelByWindowId(windowId)
                        openWindowIds.value = openWindowIds.value - windowId
                        drawerOverlay.updateCatalogue(windowCatalogue.value, openWindowIds.value)
                        if (panels.isEmpty()) statusLabel.visibility = View.VISIBLE
                    }
                }
            }
        }
    }

    private suspend fun closeWindow(windowId: ULong) {
        val panelIndex = panels.indexOfFirst { it.windowId == windowId }
        if (panelIndex < 0) return
        val panel = panels[panelIndex]
        Log.i(TAG, "closing stream for windowId=$windowId streamId=${panel.streamId}")
        connection?.closeStream(panel.streamId)
        // The StreamLifecycleEvent.Stopped handler in openWindow's collector will
        // call removePanelByWindowId — but we remove eagerly here too for responsiveness.
        runOnUiThread {
            removePanelAt(panelIndex)
            openWindowIds.value = openWindowIds.value - windowId
            drawerOverlay.updateCatalogue(windowCatalogue.value, openWindowIds.value)
            if (panels.isEmpty()) statusLabel.visibility = View.VISIBLE
        }
    }

    // ─── Panel management ────────────────────────────────────────────────────

    private fun addPanel(windowId: ULong, streamId: Int, width: Int, height: Int) {
        val index = panels.size
        val surfaceView = createPanelSurfaceView(index)
        val tabView = createTabView(index, windowId)
        surfaceContainer.addView(surfaceView)
        tabBar.addView(tabView)

        panels.add(PanelState(
            windowId = windowId,
            streamId = streamId,
            surfaceView = surfaceView,
            tabView = tabView
        ))

        // If this is the first panel, make it active.
        if (panels.size == 1) {
            switchToPanel(0)
        } else {
            surfaceView.visibility = View.GONE
        }

        // Kick off the decoder once the surface is ready.
        activityScope.launch {
            surfaceLock.withLock {
                if (surfaceView.holder.surface.isValid) {
                    startDecoderLocked(index, streamId, width, height, surfaceView.holder)
                }
                // Otherwise surfaceCreated callback will start it.
            }
        }
    }

    private fun removePanelByWindowId(windowId: ULong) {
        val index = panels.indexOfFirst { it.windowId == windowId }
        if (index >= 0) removePanelAt(index)
    }

    private fun removePanelAt(index: Int) {
        if (index < 0 || index >= panels.size) return
        val panel = panels[index]
        panel.decoder?.runCatching { stop() }
        panel.pipelineScope?.let { scope ->
            activityScope.launch { runCatching { scope.coroutineContext.job.cancelAndJoin() } }
        }
        surfaceContainer.removeView(panel.surfaceView)
        tabBar.removeView(panel.tabView)
        panels.removeAt(index)

        // Adjust activeIndex.
        if (panels.isEmpty()) {
            activeIndex = -1
        } else if (index <= activeIndex) {
            activeIndex = (activeIndex - 1).coerceAtLeast(0)
            switchToPanel(activeIndex)
        }
        updateTabHighlights()
    }

    private fun switchToPanel(newIndex: Int) {
        if (newIndex < 0 || newIndex >= panels.size) return
        val previousIndex = activeIndex
        activeIndex = newIndex

        // Hide previous, show new.
        if (previousIndex in panels.indices) {
            panels[previousIndex].surfaceView.visibility = View.GONE
            val previousStreamId = panels[previousIndex].streamId
            activityScope.launch { runCatching { connection?.pauseStream(previousStreamId) } }
        }
        panels[newIndex].surfaceView.visibility = View.VISIBLE
        val newStreamId = panels[newIndex].streamId
        activityScope.launch {
            runCatching { connection?.resumeStream(newStreamId) }
            runCatching { connection?.focusWindow(newStreamId) }
        }
        updateTabHighlights()
    }

    private fun updateTabHighlights() {
        panels.forEachIndexed { index, panel ->
            panel.tabView.setBackgroundColor(
                if (index == activeIndex) Color.rgb(50, 70, 120) else Color.TRANSPARENT
            )
        }
    }

    // ─── Surface + Decoder ───────────────────────────────────────────────────

    private fun createPanelSurfaceView(panelIndex: Int): SurfaceView =
        SurfaceView(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            ).also { it.topMargin = TAB_BAR_HEIGHT_PX }
            visibility = View.VISIBLE
            holder.addCallback(createSurfaceCallback(panelIndex))
        }

    private fun createSurfaceCallback(panelIndex: Int): SurfaceHolder.Callback =
        object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                Diagnostics.report(PipelineEvent.SurfaceCreated(panelIndex))
                val panel = panels.getOrNull(panelIndex) ?: return
                activityScope.launch {
                    surfaceLock.withLock {
                        startDecoderLocked(panelIndex, panel.streamId, 0, 0, holder)
                    }
                }
            }
            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
            override fun surfaceDestroyed(holder: SurfaceHolder) {
                Diagnostics.report(PipelineEvent.SurfaceDestroyed(panelIndex, reasonHint = "lifecycle"))
                activityScope.launch {
                    surfaceLock.withLock { tearDownDecoderLocked(panelIndex) }
                }
            }
        }

    private suspend fun startDecoderLocked(
        panelIndex: Int, streamId: Int, width: Int, height: Int, holder: SurfaceHolder
    ) {
        val surface = holder.surface
        if (!surface.isValid) return
        val liveConnection = connection ?: return

        val udpReceiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0
        )
        val pipelineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val frames: Flow<EncodedFrame> = udpReceiver.start(pipelineScope)

        val openInstantNanos = System.nanoTime()
        var firstReported = false
        val instrumentedFrames: Flow<EncodedFrame> = frames.onEach {
            if (!firstReported) {
                firstReported = true
                val delay = (System.nanoTime() - openInstantNanos) / 1_000_000
                Diagnostics.report(PipelineEvent.UdpFirstPacketReceived(streamId, delay))
            }
        }
        Diagnostics.report(PipelineEvent.UdpBound(udpReceiver.boundPort))

        runCatching { liveConnection.send(ControlMessage.ViewerReady(viewerUdpPort = udpReceiver.boundPort)) }
        runCatching { liveConnection.requestKeyframe(streamId) }

        val resolvedWidth = if (width > 0) width else DEFAULT_SURFACE_DIMENSION
        val resolvedHeight = if (height > 0) height else DEFAULT_SURFACE_DIMENSION
        Diagnostics.report(PipelineEvent.DecoderStarting(streamId, resolvedWidth, resolvedHeight))

        val frameSink = DirectSurfaceFrameSink(surface)
        val decoder = MediaCodecDecoder(
            frameSink = frameSink,
            onKeyframeRequested = { runCatching { liveConnection.requestKeyframe(streamId) } },
            streamId = streamId
        )
        panels.getOrNull(panelIndex)?.decoder?.runCatching { stop() }
        if (panelIndex in panels.indices) {
            panels[panelIndex] = panels[panelIndex].copy(decoder = decoder, pipelineScope = pipelineScope)
        }
        try {
            decoder.start(pipelineScope, instrumentedFrames, resolvedWidth, resolvedHeight)
            Diagnostics.report(PipelineEvent.DecoderStarted(streamId))
        } catch (decoderException: Exception) {
            Diagnostics.report(PipelineEvent.DecoderFailed(streamId, decoderException))
        }
    }

    private suspend fun tearDownDecoderLocked(panelIndex: Int) {
        val panel = panels.getOrNull(panelIndex) ?: return
        panel.decoder?.runCatching { stop() }
        if (panelIndex in panels.indices) {
            panels[panelIndex] = panels[panelIndex].copy(decoder = null, pipelineScope = null)
        }
        val scope = panel.pipelineScope ?: return
        runCatching { scope.coroutineContext.job.cancelAndJoin() }
    }

    // ─── Tab views ───────────────────────────────────────────────────────────

    private fun createTabView(index: Int, windowId: ULong): LinearLayout {
        val title = windowCatalogue.value[windowId]?.let { descriptor ->
            val label = descriptor.title.ifBlank { descriptor.processName }
            if (label.length > MAX_TAB_LABEL_LENGTH) label.take(MAX_TAB_LABEL_LENGTH) + "…" else label
        } ?: "Window ${index + 1}"

        val titleLabel = TextView(this).apply {
            text = title
            setTextColor(Color.WHITE)
            textSize = 13f
            maxLines = 1
            gravity = Gravity.CENTER_VERTICAL
        }

        val closeButton = TextView(this).apply {
            text = "×"
            setTextColor(Color.rgb(200, 100, 100))
            textSize = 18f
            setPadding(12, 0, 4, 0)
            gravity = Gravity.CENTER_VERTICAL
            isClickable = true
            isFocusable = true
            setOnClickListener {
                val panelIndex = panels.indexOfFirst { it.windowId == windowId }
                if (panelIndex >= 0) {
                    activityScope.launch { closeWindow(windowId) }
                }
            }
        }

        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(24, 0, 12, 0)
            gravity = Gravity.CENTER_VERTICAL
            isClickable = true
            isFocusable = true
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.MATCH_PARENT
            )
            addView(titleLabel)
            addView(closeButton)
            setOnClickListener {
                val panelIndex = panels.indexOfFirst { it.windowId == windowId }
                if (panelIndex >= 0) switchToPanel(panelIndex)
            }
        }
    }

    // ─── Key event routing ───────────────────────────────────────────────────

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        val isDown = when (event.action) {
            android.view.KeyEvent.ACTION_DOWN -> true
            android.view.KeyEvent.ACTION_UP -> false
            else -> return super.dispatchKeyEvent(event)
        }
        val activeStreamId = panels.getOrNull(activeIndex)?.streamId ?: return super.dispatchKeyEvent(event)
        val translator = KeyEventTranslator(activeStreamId)
        val controlMessage = translator.translate(
            androidKeyCode = event.keyCode,
            unicodeChar = event.unicodeChar,
            isDown = isDown
        ) ?: return super.dispatchKeyEvent(event)
        sendKeyEvent(controlMessage)
        return true
    }

    private fun sendKeyEvent(message: ControlMessage.KeyEvent) {
        activityScope.launch { runCatching { connection?.send(message) } }
    }

    // ─── Soft keyboard ───────────────────────────────────────────────────────

    private fun buildSoftInputTextWatcher(): TextWatcher = object : TextWatcher {
        override fun beforeTextChanged(sequence: CharSequence?, start: Int, count: Int, after: Int) {}
        override fun onTextChanged(sequence: CharSequence?, start: Int, before: Int, count: Int) {}
        override fun afterTextChanged(editable: Editable?) {
            val current = editable?.toString() ?: ""
            val activeStreamId = panels.getOrNull(activeIndex)?.streamId ?: run {
                previousSoftInputLength = current.length; return
            }
            val translator = KeyEventTranslator(activeStreamId)
            when {
                current.length > previousSoftInputLength -> {
                    current.substring(previousSoftInputLength).forEach { character ->
                        translator.unicodeKeyPair(character).forEach { sendKeyEvent(it) }
                    }
                    appendPreview(current.substring(previousSoftInputLength))
                }
                current.length < previousSoftInputLength -> {
                    repeat(previousSoftInputLength - current.length) {
                        translator.backspaceKeyPair().forEach { sendKeyEvent(it) }
                    }
                    trimPreview(previousSoftInputLength - current.length)
                }
            }
            previousSoftInputLength = current.length
        }
    }

    private fun toggleSoftKeyboard() {
        val inputMethodManager = getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager
        if (softInputEditText.hasFocus()) {
            inputMethodManager.hideSoftInputFromWindow(softInputEditText.windowToken, 0)
            softInputEditText.clearFocus()
            inputPreviewTextView.visibility = View.GONE
            softInputEditText.setText("")
            previousSoftInputLength = 0
            inputPreviewTextView.text = ""
        } else {
            softInputEditText.requestFocus()
            inputMethodManager.showSoftInput(softInputEditText, InputMethodManager.SHOW_IMPLICIT)
            inputPreviewTextView.visibility = View.VISIBLE
        }
    }

    private fun appendPreview(text: String) {
        val combined = inputPreviewTextView.text.toString() + text
        inputPreviewTextView.text = if (combined.length > PREVIEW_MAX_CHARS) combined.takeLast(PREVIEW_MAX_CHARS) else combined
    }

    private fun trimPreview(count: Int) {
        val current = inputPreviewTextView.text.toString()
        inputPreviewTextView.text = current.substring(0, (current.length - count).coerceAtLeast(0))
    }

    // ─── Wifi lock ───────────────────────────────────────────────────────────

    private fun acquireWifiLock() {
        val wifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val lock = wifiManager.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "UnifiedStreaming")
        lock.acquire()
        lowLatencyWifiLock = lock
        Diagnostics.report(PipelineEvent.WifiLockAcquired)
    }

    // ─── Lifecycle ───────────────────────────────────────────────────────────

    override fun onDestroy() {
        lowLatencyWifiLock?.runCatching {
            if (isHeld) {
                release()
                Diagnostics.report(PipelineEvent.WifiLockReleased)
            }
        }
        lowLatencyWifiLock = null
        panels.forEach { panel ->
            panel.decoder?.runCatching { stop() }
        }
        activityScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "UnifiedStreaming"
        const val TAB_BAR_HEIGHT_PX = 120
        const val MAX_TAB_LABEL_LENGTH = 25
        const val PREVIEW_MAX_CHARS = 80
        const val DEFAULT_SURFACE_DIMENSION = 1920
    }
}
