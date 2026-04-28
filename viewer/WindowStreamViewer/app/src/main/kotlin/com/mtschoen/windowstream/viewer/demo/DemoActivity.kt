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
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.GridLayout
import android.widget.TextView
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
import kotlin.math.ceil
import kotlin.math.sqrt

/**
 * Phone- / Quest-compatible demo activity that bypasses Jetpack XR. Connects
 * directly to one or more servers via intent extras and renders decoded
 * frames onto a grid of plain SurfaceViews.
 *
 * Single-server usage (legacy, adb-friendly):
 *   adb shell am start \
 *     -n com.mtschoen.windowstream.viewer/.demo.DemoActivity \
 *     --es streamHost 10.0.2.2 \
 *     --ei streamPort 64000
 *
 * Multi-server usage (via [com.mtschoen.windowstream.viewer.app.ServerSelectionActivity]):
 *   putExtra("streamHosts", Array<String>)  // parallel to streamPorts
 *   putExtra("streamPorts", IntArray)       // parallel to streamHosts
 *
 * Tap anywhere on the streamed area to toggle the on-screen keyboard. Typed
 * characters are relayed to the FIRST pipeline only (keeping tonight's
 * scope sane); a preview overlay at the top of the screen echoes typed
 * characters because the keyboard will hide the lower portion of the
 * panels. Physical / Bluetooth keyboards continue to route through
 * [dispatchKeyEvent], also targeting the first pipeline.
 *
 * Surface lifecycle: each pipeline runs in its own [CoroutineScope] parented
 * to [demoScope]. On surfaceDestroyed for a given panel, its pipeline scope
 * is cancelled and joined so every worker coroutine — including MediaCodec
 * native output callbacks — has finished before a new pipeline is permitted
 * to start on the new Surface. [pipelineLock] serializes the callbacks
 * across all panels.
 */
class DemoActivity : Activity() {
    private val demoScope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val pipelineLock: Mutex = Mutex()

    private data class StreamConfiguration(val host: String, val port: Int)
    private data class StreamState(
        var scope: CoroutineScope? = null,
        var decoder: MediaCodecDecoder? = null,
        var connection: ControlConnection? = null,
    )

    private lateinit var streamConfigurations: List<StreamConfiguration>
    private lateinit var streamStates: MutableList<StreamState>

    private lateinit var softInputEditText: EditText
    private lateinit var inputPreviewTextView: TextView
    private var previousSoftInputLength: Int = 0
    private var lowLatencyWifiLock: WifiManager.WifiLock? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        streamConfigurations = parseStreamConfigurations()
        streamStates = MutableList(streamConfigurations.size) { StreamState() }

        Log.i(TAG, "connecting to ${streamConfigurations.size} stream(s): " +
            streamConfigurations.joinToString { "${it.host}:${it.port}" })

        val columnCount: Int = ceil(sqrt(streamConfigurations.size.toDouble())).toInt().coerceAtLeast(1)
        val rowCount: Int = ceil(streamConfigurations.size.toDouble() / columnCount).toInt().coerceAtLeast(1)

        val grid: GridLayout = GridLayout(this).apply {
            this.rowCount = rowCount
            this.columnCount = columnCount
        }
        streamConfigurations.forEachIndexed { index, _ ->
            val surfaceView = SurfaceView(this).apply {
                layoutParams = GridLayout.LayoutParams().apply {
                    width = 0
                    height = 0
                    columnSpec = GridLayout.spec(index % columnCount, 1f)
                    rowSpec = GridLayout.spec(index / columnCount, 1f)
                }
                holder.addCallback(createSurfaceCallback(index))
            }
            grid.addView(surfaceView)
        }

        // Invisible focus target for the soft keyboard. A 1×1 alpha-0 EditText
        // is focusable (needed for the IME to open) but doesn't take visible
        // screen space. Text changes are relayed as KEY_EVENT messages via
        // [softInputTextWatcher] to the first stream's control connection.
        softInputEditText = EditText(this).apply {
            alpha = 0f
            isFocusable = true
            isFocusableInTouchMode = true
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            setSingleLine(false)
            layoutParams = FrameLayout.LayoutParams(
                1, 1, Gravity.START or Gravity.BOTTOM
            )
            addTextChangedListener(softInputTextWatcher())
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
            )
        }

        val rootLayout: FrameLayout = FrameLayout(this).apply {
            addView(
                grid,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT,
                    FrameLayout.LayoutParams.MATCH_PARENT
                )
            )
            addView(softInputEditText)
            addView(inputPreviewTextView)
            isClickable = true
            setOnClickListener { toggleSoftKeyboard() }
        }
        setContentView(rootLayout)

        // Hold a high-perf WifiLock for the entire streaming session. Goal is
        // to suppress WiFi power-save batching on the HMD radio — without it,
        // measurement showed inter-arrival p99 of ~810ms on the viewer side
        // even though the server emitted at a steady ~70ms cadence (radio was
        // grouping packets, then dumping them in bursts). WIFI_MODE_FULL_LOW_LATENCY
        // is the standard Android API for low-latency real-time apps.
        val wifiManager: WifiManager = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val lock: WifiManager.WifiLock = wifiManager.createWifiLock(
            WifiManager.WIFI_MODE_FULL_LOW_LATENCY,
            "WindowStreamDemoActivity"
        )
        lock.acquire()
        lowLatencyWifiLock = lock
        Log.i(TAG, "acquired WIFI_MODE_FULL_LOW_LATENCY lock")
    }

    private fun parseStreamConfigurations(): List<StreamConfiguration> {
        val hosts: Array<String>? = intent.getStringArrayExtra("streamHosts")
        val ports: IntArray? = intent.getIntArrayExtra("streamPorts")
        if (hosts != null && ports != null && hosts.size == ports.size && hosts.isNotEmpty()) {
            return hosts.mapIndexed { index, host -> StreamConfiguration(host, ports[index]) }
        }
        // Single-server intent shape (v2 picker or legacy adb launch).
        // When selectedWindowIds is present, create one StreamConfiguration per
        // selected window — all pointing at the same host:port — so the grid has
        // one SurfaceView per window. Without selectedWindowIds, fall back to a
        // single pipeline (auto-selects first window in runPipeline).
        val singleHost: String = intent.getStringExtra("streamHost")
            ?: error("DemoActivity requires streamHosts+streamPorts arrays OR --es streamHost")
        val singlePort: Int = intent.getIntExtra("streamPort", -1)
        require(singlePort > 0) { "DemoActivity requires --ei streamPort <port>" }
        val selectedWindowIds: LongArray = intent.getLongArrayExtra("selectedWindowIds")
            ?: LongArray(0)
        val pipelineCount: Int = if (selectedWindowIds.isNotEmpty()) selectedWindowIds.size else 1
        return List(pipelineCount) { StreamConfiguration(singleHost, singlePort) }
    }

    private fun createSurfaceCallback(streamIndex: Int): SurfaceHolder.Callback =
        object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                // Pass the long-lived SurfaceHolder, NOT holder.surface. The
                // Surface object can be invalidated by the OS during the
                // ~200ms TCP handshake before MediaCodec.configure runs;
                // capturing the holder lets runPipeline re-read a fresh
                // Surface at the last moment.
                demoScope.launch {
                    pipelineLock.withLock {
                        tearDownPipelineLocked(streamIndex)
                        startPipelineLocked(streamIndex, holder)
                    }
                }
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
                // Size-only changes don't require a pipeline rebind — MediaCodec
                // outputs to the same Surface; the compositor handles scaling.
            }

            override fun surfaceDestroyed(holder: SurfaceHolder) {
                demoScope.launch {
                    pipelineLock.withLock {
                        tearDownPipelineLocked(streamIndex)
                    }
                }
            }
        }

    private suspend fun startPipelineLocked(streamIndex: Int, holder: SurfaceHolder) {
        val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        streamStates[streamIndex].scope = scope
        runCatching {
            runPipeline(streamIndex, scope, holder)
        }.onFailure { throwable ->
            Log.e(TAG, "pipeline $streamIndex failed to start", throwable)
            // Clean up partial state (control connection, receiver) so the
            // server doesn't keep a half-dead viewer registered. The next
            // surfaceCreated callback will retry from scratch.
            runCatching { tearDownPipelineLocked(streamIndex) }
        }
    }

    private suspend fun runPipeline(
        streamIndex: Int,
        scope: CoroutineScope,
        holder: SurfaceHolder
    ) {
        val configuration: StreamConfiguration = streamConfigurations[streamIndex]
        val client = ControlClient(
            host = configuration.host,
            port = configuration.port,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 3840,
                maximumHeight = 2160,
                supportedCodecs = listOf("h264")
            )
        )
        val connection: ControlConnection = client.connect(scope)
        streamStates[streamIndex].connection = connection

        val serverHello: ControlMessage.ServerHello = withTimeout(10_000) {
            connection.incoming.filterIsInstance<ControlMessage.ServerHello>().first()
        }

        // Determine which window this pipeline should open.
        // selectedWindowIds from the intent: if present, pick the windowId at position
        // [streamIndex] in the array; fall back to the first advertised window so
        // the legacy adb-direct launch path (no selectedWindowIds extra) still works.
        val selectedWindowIds: LongArray = intent.getLongArrayExtra("selectedWindowIds")
            ?: LongArray(0)
        val windowId: ULong = when {
            streamIndex < selectedWindowIds.size ->
                selectedWindowIds[streamIndex].toULong()
            else ->
                (serverHello.windows.firstOrNull()
                    ?: error("server advertised no windows in ServerHello"))
                    .windowId
        }
        Log.i(TAG, "stream $streamIndex ServerHello: udpPort=${serverHello.udpPort}, advertising ${serverHello.windows.size} window(s); opening windowId=$windowId")
        connection.send(ControlMessage.OpenStream(windowId = windowId))

        val stream: ControlMessage.StreamStarted = withTimeout(10_000) {
            connection.incoming.filterIsInstance<ControlMessage.StreamStarted>().first()
        }

        Log.i(TAG, "stream $streamIndex ${stream.streamId}: ${stream.width}x${stream.height} @ ${stream.framesPerSecond} fps, windowId=${stream.windowId}")

        val udpReceiver = UdpTransportReceiver(
            bindAddress = InetAddress.getByName("0.0.0.0"),
            requestedPort = 0
        )
        val frames: Flow<EncodedFrame> = udpReceiver.start(scope)
        val viewerUdpPort: Int = udpReceiver.boundPort
        Log.i(TAG, "stream $streamIndex viewer UDP bound on port $viewerUdpPort")

        // TODO(v2-phase5): VIEWER_READY no longer carries streamId in v2.
        connection.send(ControlMessage.ViewerReady(viewerUdpPort = viewerUdpPort))
        connection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))

        // Re-read the Surface from the holder at the LAST moment, after the
        // TCP handshake has settled. If the OS recycled it during the gap,
        // bail with a tagged error; the SurfaceView's next surfaceCreated
        // callback will trigger a fresh attempt with the new Surface.
        val freshSurface: Surface = holder.surface
        if (!freshSurface.isValid) {
            error("Surface for pipeline $streamIndex was released during pipeline startup; will retry on next surfaceCreated")
        }
        val frameSink = DirectSurfaceFrameSink(freshSurface)

        val decoder = MediaCodecDecoder(
            frameSink = frameSink,
            onKeyframeRequested = {
                connection.send(ControlMessage.RequestKeyframe(streamId = stream.streamId))
            }
        )
        streamStates[streamIndex].decoder = decoder
        decoder.start(scope, frames, stream.width, stream.height)
    }

    private suspend fun tearDownPipelineLocked(streamIndex: Int) {
        val state: StreamState = streamStates[streamIndex]
        state.decoder?.runCatching { stop() }
        state.decoder = null
        state.connection = null

        val scope: CoroutineScope = state.scope ?: return
        state.scope = null
        val job: Job = scope.coroutineContext.job
        runCatching { job.cancelAndJoin() }
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

    private fun softInputTextWatcher(): TextWatcher = object : TextWatcher {
        override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
        override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
        override fun afterTextChanged(s: Editable?) {
            val current: String = s?.toString() ?: ""
            when {
                current.length > previousSoftInputLength -> {
                    val added: String = current.substring(previousSoftInputLength)
                    added.forEach { character -> relayUnicodeCharacter(character) }
                    appendPreview(added)
                }
                current.length < previousSoftInputLength -> {
                    val removedCount: Int = previousSoftInputLength - current.length
                    repeat(removedCount) { relayBackspace() }
                    trimPreview(removedCount)
                }
            }
            previousSoftInputLength = current.length
        }
    }

    private fun relayUnicodeCharacter(character: Char) {
        // TODO(v2-phase5): use the focused panel's streamId once the picker/switcher UI is rewritten.
        sendKeyEventToPrimary(ControlMessage.KeyEvent(streamId = PLACEHOLDER_KEY_STREAM_ID, keyCode = character.code, isUnicode = true, isDown = true))
        sendKeyEventToPrimary(ControlMessage.KeyEvent(streamId = PLACEHOLDER_KEY_STREAM_ID, keyCode = character.code, isUnicode = true, isDown = false))
    }

    private fun relayBackspace() {
        // TODO(v2-phase5): use the focused panel's streamId once the picker/switcher UI is rewritten.
        sendKeyEventToPrimary(ControlMessage.KeyEvent(streamId = PLACEHOLDER_KEY_STREAM_ID, keyCode = 0x08, isUnicode = false, isDown = true))
        sendKeyEventToPrimary(ControlMessage.KeyEvent(streamId = PLACEHOLDER_KEY_STREAM_ID, keyCode = 0x08, isUnicode = false, isDown = false))
    }

    private fun sendKeyEventToPrimary(message: ControlMessage.KeyEvent) {
        demoScope.launch {
            runCatching { streamStates.firstOrNull()?.connection?.send(message) }
        }
    }

    private fun appendPreview(text: String) {
        val current: String = inputPreviewTextView.text.toString()
        val combined: String = current + text
        val maximumVisibleCharacters: Int = 80
        inputPreviewTextView.text = if (combined.length > maximumVisibleCharacters) {
            combined.takeLast(maximumVisibleCharacters)
        } else {
            combined
        }
    }

    private fun trimPreview(count: Int) {
        val current: String = inputPreviewTextView.text.toString()
        val newLength: Int = (current.length - count).coerceAtLeast(0)
        inputPreviewTextView.text = current.substring(0, newLength)
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        val isDown: Boolean = when (event.action) {
            android.view.KeyEvent.ACTION_DOWN -> true
            android.view.KeyEvent.ACTION_UP -> false
            else -> return super.dispatchKeyEvent(event)
        }
        val controlMessage: ControlMessage.KeyEvent = translateToControlMessage(event, isDown)
            ?: return super.dispatchKeyEvent(event)
        sendKeyEventToPrimary(controlMessage)
        return true
    }

    private fun translateToControlMessage(
        event: android.view.KeyEvent,
        isDown: Boolean
    ): ControlMessage.KeyEvent? {
        val unicode: Int = event.unicodeChar
        if (unicode != 0) {
            // TODO(v2-phase5): use the focused panel's streamId once the picker/switcher UI is rewritten.
            return ControlMessage.KeyEvent(
                streamId = PLACEHOLDER_KEY_STREAM_ID,
                keyCode = unicode,
                isUnicode = true,
                isDown = isDown
            )
        }
        val windowsVirtualKey: Int = when (event.keyCode) {
            android.view.KeyEvent.KEYCODE_ENTER -> 0x0D
            android.view.KeyEvent.KEYCODE_DEL -> 0x08
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
        // TODO(v2-phase5): use the focused panel's streamId once the picker/switcher UI is rewritten.
        return ControlMessage.KeyEvent(
            streamId = PLACEHOLDER_KEY_STREAM_ID,
            keyCode = windowsVirtualKey,
            isUnicode = false,
            isDown = isDown
        )
    }

    override fun onDestroy() {
        lowLatencyWifiLock?.runCatching { if (isHeld) release() }
        lowLatencyWifiLock = null
        // Best-effort synchronous release of MediaCodec natives; remaining
        // teardown cascades via demoScope cancellation.
        streamStates.forEach { state ->
            state.decoder?.runCatching { stop() }
            state.decoder = null
            state.connection = null
        }
        demoScope.cancel()
        super.onDestroy()
    }

    private companion object {
        const val TAG = "WindowStreamDemo"

        // TODO(v2-phase5): replace with the focused panel's streamId once the
        // picker/switcher UI is rewritten in tasks 5.4-5.6. Until then, we stamp
        // every relayed key event with this placeholder so the wire shape stays
        // valid; the v2 server uses streamId to attribute keys to a specific
        // window so this is wrong but compiles and matches the v1 single-stream
        // demo's behavior of relaying to the first pipeline.
        const val PLACEHOLDER_KEY_STREAM_ID: Int = 1
    }
}
