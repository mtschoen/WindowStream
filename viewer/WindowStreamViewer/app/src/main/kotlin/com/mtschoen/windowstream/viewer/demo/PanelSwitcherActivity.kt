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
import com.mtschoen.windowstream.viewer.transport.EncodedFrame
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.job
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.net.InetAddress

/**
 * Panel-switcher activity launched by [com.mtschoen.windowstream.viewer.app.ServerSelectionActivity]
 * when the user connects to a server after selecting windows in the window picker.
 *
 * Layout: a horizontal tab bar across the top showing one tab per selected stream, and a
 * full-screen [SurfaceView] per stream below. Only the active panel's SurfaceView is VISIBLE;
 * the rest are GONE so the GPU decodes only for the live panel. When the user taps a tab:
 * - The previously-active stream receives PAUSE_STREAM.
 * - The newly-active stream's SurfaceView becomes VISIBLE, and the old one goes GONE.
 * - The newly-active stream receives RESUME_STREAM + FOCUS_WINDOW.
 * - Subsequent key events carry the newly-active stream's streamId.
 *
 * Intent extras:
 * - `streamHost: String` — server IP.
 * - `streamPort: Int` — server control TCP port.
 * - `selectedWindowIds: LongArray` — windowIds selected in the picker (one panel per entry).
 *
 * Uses [MultiStreamControlClient] (v2 protocol) for a single shared TCP control connection.
 * Each stream gets an independent [UdpTransportReceiver] and [MediaCodecDecoder].
 *
 * Key events are translated via [KeyEventTranslator] and routed to the active panel's
 * [MultiStreamControlConnection] with the correct streamId.
 */
class PanelSwitcherActivity : Activity() {

    private val activityScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val surfaceLock: Mutex = Mutex()

    /** Mutable state for a single stream panel. */
    private data class PanelState(
        val windowId: ULong,
        val surfaceView: SurfaceView,
        val tabView: TextView,
        val streamId: Int = UNKNOWN_STREAM_ID,
        val decoder: MediaCodecDecoder? = null,
        val pipelineScope: CoroutineScope? = null
    )

    private var connection: MultiStreamControlConnection? = null
    private var panels: MutableList<PanelState> = mutableListOf()
    private var activeIndex: Int = 0
    private var lowLatencyWifiLock: WifiManager.WifiLock? = null

    private lateinit var surfaceContainer: FrameLayout
    private lateinit var tabBar: LinearLayout
    private lateinit var softInputEditText: EditText
    private lateinit var inputPreviewTextView: TextView
    private var previousSoftInputLength: Int = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val host: String = intent.getStringExtra("streamHost")
            ?: error("PanelSwitcherActivity requires --es streamHost")
        val port: Int = intent.getIntExtra("streamPort", -1)
        require(port > 0) { "PanelSwitcherActivity requires --ei streamPort <port>" }
        val selectedWindowIds: LongArray = intent.getLongArrayExtra("selectedWindowIds")
            ?: LongArray(0)
        require(selectedWindowIds.isNotEmpty()) {
            "PanelSwitcherActivity requires selectedWindowIds; use DemoActivity for legacy adb path"
        }

        Log.i(TAG, "opening ${selectedWindowIds.size} panel(s) from $host:$port")

        buildLayout(selectedWindowIds.size)
        selectedWindowIds.forEachIndexed { index, rawId ->
            val surfaceView: SurfaceView = createPanelSurfaceView(index)
            val tabView: TextView = createTabView(index, "Window ${index + 1}")
            surfaceContainer.addView(surfaceView)
            tabBar.addView(tabView)
            panels.add(PanelState(
                windowId = rawId.toULong(),
                surfaceView = surfaceView,
                tabView = tabView
            ))
        }
        updateTabHighlights()

        acquireWifiLock()

        activityScope.launch {
            connectAndOpenStreams(host, port, selectedWindowIds)
        }
    }

    // ─── Layout helpers ───────────────────────────────────────────────────────

    private fun buildLayout(panelCount: Int) {
        tabBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(Color.rgb(30, 30, 30))
        }
        val tabScroll = HorizontalScrollView(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                TAB_BAR_HEIGHT_PX,
                Gravity.TOP
            )
            addView(tabBar)
        }

        surfaceContainer = FrameLayout(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
        }

        softInputEditText = EditText(this).apply {
            alpha = 0f
            isFocusable = true
            isFocusableInTouchMode = true
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            setSingleLine(false)
            layoutParams = FrameLayout.LayoutParams(
                1, 1, Gravity.START or Gravity.BOTTOM
            )
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
                Gravity.TOP or Gravity.START
            ).also { params ->
                params.topMargin = if (panelCount > 0) TAB_BAR_HEIGHT_PX else 0
            }
        }

        val rootLayout = FrameLayout(this).apply {
            addView(surfaceContainer, FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            ))
            addView(tabScroll)
            addView(softInputEditText)
            addView(inputPreviewTextView)
            isClickable = true
            setOnClickListener { toggleSoftKeyboard() }
        }
        setContentView(rootLayout)
    }

    private fun createPanelSurfaceView(index: Int): SurfaceView =
        SurfaceView(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            ).also { params ->
                params.topMargin = TAB_BAR_HEIGHT_PX
            }
            visibility = if (index == 0) View.VISIBLE else View.GONE
            holder.addCallback(createSurfaceCallback(index))
        }

    private fun createTabView(index: Int, label: String): TextView =
        TextView(this).apply {
            text = label
            setTextColor(Color.WHITE)
            textSize = 14f
            setPadding(36, 0, 36, 0)
            gravity = Gravity.CENTER_VERTICAL
            isClickable = true
            isFocusable = true
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.MATCH_PARENT
            )
            setOnClickListener { onTabTapped(index) }
        }

    private fun updateTabHighlights() {
        panels.forEachIndexed { index, panel ->
            panel.tabView.setBackgroundColor(
                if (index == activeIndex) Color.rgb(60, 90, 140) else Color.TRANSPARENT
            )
        }
    }

    // ─── Connection + stream lifecycle ───────────────────────────────────────

    private suspend fun connectAndOpenStreams(
        host: String,
        port: Int,
        selectedWindowIds: LongArray
    ) {
        val client = MultiStreamControlClient(
            host = host,
            port = port,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 3840,
                maximumHeight = 2160,
                supportedCodecs = listOf("h264")
            )
        )
        val liveConnection: MultiStreamControlConnection = runCatching {
            client.connect(activityScope)
        }.getOrElse { throwable ->
            Log.e(TAG, "failed to connect to $host:$port", throwable)
            return
        }
        connection = liveConnection

        Log.i(TAG, "connected: ${liveConnection.serverHello.windows.size} window(s) advertised")

        // Open one stream per selected windowId. Streams are opened by launching a
        // separate coroutine per panel so openStream flows can be collected in parallel.
        selectedWindowIds.forEachIndexed { index, rawWindowId ->
            val windowId: ULong = rawWindowId.toULong()
            val windowTitle: String = liveConnection.serverHello.windows
                .firstOrNull { it.windowId == windowId }
                ?.let { descriptor -> formatTabLabel(descriptor) }
                ?: "Window ${index + 1}"

            runOnUiThread { panels[index].tabView.text = windowTitle }

            activityScope.launch {
                liveConnection.openStream(windowId, activityScope).collect { event ->
                    when (event) {
                        is StreamLifecycleEvent.Opened -> {
                            Log.i(TAG, "panel $index opened: streamId=${event.streamId} ${event.width}x${event.height}")
                            panels[index] = panels[index].copy(streamId = event.streamId)
                            // The SurfaceView's surfaceCreated callback will start the decoder
                            // pipeline once the Surface is available. Trigger it now if the
                            // Surface is already created (race: connection may have completed
                            // after surfaceCreated fired).
                            runOnUiThread {
                                val holder: SurfaceHolder = panels[index].surfaceView.holder
                                if (holder.surface.isValid) {
                                    activityScope.launch {
                                        surfaceLock.withLock {
                                            startDecoderLocked(
                                                index, event.streamId,
                                                event.width, event.height, holder
                                            )
                                        }
                                    }
                                }
                            }
                        }
                        is StreamLifecycleEvent.Refused -> {
                            Log.e(TAG, "panel $index refused: ${event.errorCode} — ${event.message}")
                        }
                        is StreamLifecycleEvent.Stopped -> {
                            Log.i(TAG, "panel $index stopped: ${event.reason.reason}")
                            surfaceLock.withLock { tearDownDecoderLocked(index) }
                        }
                    }
                }
            }
        }

        // Send FOCUS_WINDOW for the first panel (best-effort; stream may not yet be open).
        val firstStreamId: Int = panels.firstOrNull()?.streamId ?: UNKNOWN_STREAM_ID
        if (firstStreamId != UNKNOWN_STREAM_ID) {
            runCatching { liveConnection.focusWindow(firstStreamId) }
        }
    }

    private fun formatTabLabel(descriptor: WindowDescriptor): String {
        val title: String = descriptor.title.ifBlank { descriptor.processName }
        return if (title.length > MAX_TAB_LABEL_LENGTH) title.take(MAX_TAB_LABEL_LENGTH) + "…" else title
    }

    // ─── SurfaceHolder callbacks ──────────────────────────────────────────────

    private fun createSurfaceCallback(panelIndex: Int): SurfaceHolder.Callback =
        object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                val streamId: Int = panels.getOrNull(panelIndex)?.streamId ?: UNKNOWN_STREAM_ID
                if (streamId == UNKNOWN_STREAM_ID) {
                    // Stream not yet opened — connectAndOpenStreams will start the
                    // decoder when the stream opens and finds a valid surface.
                    return
                }
                activityScope.launch {
                    surfaceLock.withLock {
                        tearDownDecoderLocked(panelIndex)
                        startDecoderLocked(panelIndex, streamId, 0, 0, holder)
                    }
                }
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

            override fun surfaceDestroyed(holder: SurfaceHolder) {
                activityScope.launch {
                    surfaceLock.withLock { tearDownDecoderLocked(panelIndex) }
                }
            }
        }

    private suspend fun startDecoderLocked(
        panelIndex: Int,
        streamId: Int,
        width: Int,
        height: Int,
        holder: SurfaceHolder
    ) {
        val freshSurface = holder.surface
        if (!freshSurface.isValid) return

        val liveConnection: MultiStreamControlConnection = connection ?: return

        val udpReceiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0
        )
        val pipelineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val frames: Flow<EncodedFrame> = udpReceiver.start(pipelineScope)
        val viewerUdpPort: Int = udpReceiver.boundPort

        runCatching { liveConnection.send(ControlMessage.ViewerReady(viewerUdpPort = viewerUdpPort)) }
        runCatching { liveConnection.requestKeyframe(streamId) }

        val resolvedWidth: Int = if (width > 0) width else DEFAULT_SURFACE_DIMENSION
        val resolvedHeight: Int = if (height > 0) height else DEFAULT_SURFACE_DIMENSION

        val frameSink = DirectSurfaceFrameSink(freshSurface)
        val decoder = MediaCodecDecoder(
            frameSink = frameSink,
            onKeyframeRequested = {
                runCatching { liveConnection.requestKeyframe(streamId) }
            }
        )
        val existingDecoder: MediaCodecDecoder? = panels.getOrNull(panelIndex)?.decoder
        if (existingDecoder != null) {
            try { existingDecoder.stop() } catch (_: Throwable) {}
        }
        panels[panelIndex] = panels[panelIndex].copy(decoder = decoder, pipelineScope = pipelineScope)
        decoder.start(pipelineScope, frames, resolvedWidth, resolvedHeight)
    }

    private suspend fun tearDownDecoderLocked(panelIndex: Int) {
        val panel: PanelState = panels.getOrNull(panelIndex) ?: return
        val existingDecoder: MediaCodecDecoder? = panel.decoder
        if (existingDecoder != null) {
            try { existingDecoder.stop() } catch (_: Throwable) {}
        }
        panels[panelIndex] = panel.copy(decoder = null, pipelineScope = null)
        val scope: CoroutineScope = panel.pipelineScope ?: return
        val job: Job = scope.coroutineContext.job
        runCatching { job.cancelAndJoin() }
    }

    // ─── Tab switching ────────────────────────────────────────────────────────

    private fun onTabTapped(newIndex: Int) {
        if (newIndex == activeIndex) return
        val previousIndex: Int = activeIndex
        activeIndex = newIndex
        updateTabHighlights()

        val previousPanel: PanelState = panels.getOrNull(previousIndex) ?: return
        val newPanel: PanelState = panels.getOrNull(newIndex) ?: return

        // Hide previous panel's SurfaceView, show new one.
        previousPanel.surfaceView.visibility = View.GONE
        newPanel.surfaceView.visibility = View.VISIBLE

        // PAUSE previous stream, RESUME + FOCUS new stream.
        activityScope.launch {
            val liveConnection: MultiStreamControlConnection = connection ?: return@launch
            if (previousPanel.streamId != UNKNOWN_STREAM_ID) {
                runCatching { liveConnection.pauseStream(previousPanel.streamId) }
            }
            if (newPanel.streamId != UNKNOWN_STREAM_ID) {
                runCatching { liveConnection.resumeStream(newPanel.streamId) }
                runCatching { liveConnection.focusWindow(newPanel.streamId) }
            }
        }
    }

    // ─── Key event routing ────────────────────────────────────────────────────

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        val isDown: Boolean = when (event.action) {
            android.view.KeyEvent.ACTION_DOWN -> true
            android.view.KeyEvent.ACTION_UP -> false
            else -> return super.dispatchKeyEvent(event)
        }
        val activeStreamId: Int = panels.getOrNull(activeIndex)?.streamId ?: UNKNOWN_STREAM_ID
        if (activeStreamId == UNKNOWN_STREAM_ID) return super.dispatchKeyEvent(event)

        val translator = KeyEventTranslator(activeStreamId)
        val controlMessage: ControlMessage.KeyEvent = translator.translate(
            androidKeyCode = event.keyCode,
            unicodeChar = event.unicodeChar,
            isDown = isDown
        ) ?: return super.dispatchKeyEvent(event)

        sendKeyEvent(controlMessage)
        return true
    }

    private fun sendKeyEvent(message: ControlMessage.KeyEvent) {
        activityScope.launch {
            runCatching { connection?.send(message) }
        }
    }

    // ─── Soft keyboard ────────────────────────────────────────────────────────

    private fun buildSoftInputTextWatcher(): TextWatcher = object : TextWatcher {
        override fun beforeTextChanged(sequence: CharSequence?, start: Int, count: Int, after: Int) {}
        override fun onTextChanged(sequence: CharSequence?, start: Int, before: Int, count: Int) {}
        override fun afterTextChanged(editable: Editable?) {
            val current: String = editable?.toString() ?: ""
            val activeStreamId: Int = panels.getOrNull(activeIndex)?.streamId ?: UNKNOWN_STREAM_ID
            if (activeStreamId == UNKNOWN_STREAM_ID) {
                previousSoftInputLength = current.length
                return
            }
            val translator = KeyEventTranslator(activeStreamId)
            when {
                current.length > previousSoftInputLength -> {
                    val added: String = current.substring(previousSoftInputLength)
                    added.forEach { character ->
                        translator.unicodeKeyPair(character).forEach { sendKeyEvent(it) }
                    }
                    appendPreview(added)
                }
                current.length < previousSoftInputLength -> {
                    val removedCount: Int = previousSoftInputLength - current.length
                    repeat(removedCount) {
                        translator.backspaceKeyPair().forEach { sendKeyEvent(it) }
                    }
                    trimPreview(removedCount)
                }
            }
            previousSoftInputLength = current.length
        }
    }

    private fun toggleSoftKeyboard() {
        val inputMethodManager: InputMethodManager =
            getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager
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
        val current: String = inputPreviewTextView.text.toString()
        val combined: String = current + text
        inputPreviewTextView.text = if (combined.length > PREVIEW_MAX_CHARS) {
            combined.takeLast(PREVIEW_MAX_CHARS)
        } else {
            combined
        }
    }

    private fun trimPreview(count: Int) {
        val current: String = inputPreviewTextView.text.toString()
        val newLength: Int = (current.length - count).coerceAtLeast(0)
        inputPreviewTextView.text = current.substring(0, newLength)
    }

    // ─── Wifi lock ────────────────────────────────────────────────────────────

    private fun acquireWifiLock() {
        val wifiManager: WifiManager =
            applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val lock: WifiManager.WifiLock = wifiManager.createWifiLock(
            WifiManager.WIFI_MODE_FULL_LOW_LATENCY,
            "WindowStreamPanelSwitcher"
        )
        lock.acquire()
        lowLatencyWifiLock = lock
        Log.i(TAG, "acquired WIFI_MODE_FULL_LOW_LATENCY lock")
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    override fun onDestroy() {
        lowLatencyWifiLock?.runCatching { if (isHeld) release() }
        lowLatencyWifiLock = null
        panels.forEach { panel ->
            val decoderToStop: MediaCodecDecoder? = panel.decoder
            if (decoderToStop != null) {
                try { decoderToStop.stop() } catch (_: Throwable) {}
            }
        }
        activityScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "PanelSwitcher"
        const val UNKNOWN_STREAM_ID: Int = -1
        const val TAB_BAR_HEIGHT_PX: Int = 120
        const val MAX_TAB_LABEL_LENGTH: Int = 30
        const val PREVIEW_MAX_CHARS: Int = 80
        const val DEFAULT_SURFACE_DIMENSION: Int = 1920
    }
}
