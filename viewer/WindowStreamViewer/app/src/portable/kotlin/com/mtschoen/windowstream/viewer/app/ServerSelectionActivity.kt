package com.mtschoen.windowstream.viewer.app

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.lifecycleScope
import com.mtschoen.windowstream.viewer.app.ui.ServerPickerScreen
import com.mtschoen.windowstream.viewer.app.ui.WindowPickerScreen
import com.mtschoen.windowstream.viewer.app.ui.WindowPickerViewModel
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.control.MultiStreamControlClient
import com.mtschoen.windowstream.viewer.control.MultiStreamControlConnection
import com.mtschoen.windowstream.viewer.demo.PanelSwitcherActivity
import com.mtschoen.windowstream.viewer.discovery.NetworkServiceDiscoveryClient
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch

/**
 * Launcher activity for the portable flavor. Implements a two-screen picker:
 *
 * 1. [ServerPickerScreen] — single-select list of servers discovered via mDNS.
 * 2. [WindowPickerScreen] — multi-select list of windows advertised by the
 *    chosen server (populated from SERVER_HELLO + live push events).
 *
 * On Connect the activity starts [DemoActivity] with:
 * - `streamHost` / `streamPort` (legacy single-server extras) pointing at the
 *   picked server.
 * - `selectedWindowIds: LongArray` — the windowIds the user selected.
 *
 * This activity disconnects from the control connection before handing off;
 * DemoActivity opens its own independent connection using [ControlClient].
 */
class ServerSelectionActivity : ComponentActivity() {
    private lateinit var discoveryClient: NetworkServiceDiscoveryClient
    // The picker's TCP control connection. Held at activity-scope so we can
    // close it cleanly before handing off to PanelSwitcherActivity — the v2
    // server only allows one viewer at a time, so the second connection
    // would be rejected with VIEWER_BUSY otherwise.
    private var pickerConnection: MultiStreamControlConnection? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        discoveryClient = NetworkServiceDiscoveryClient(applicationContext)

        val discoveredFlow: MutableSharedFlow<ServerInformation> = MutableSharedFlow(
            replay = 8,
            extraBufferCapacity = 32,
            onBufferOverflow = BufferOverflow.DROP_OLDEST
        )
        lifecycleScope.launch {
            discoveryClient.discover(this).collect { discoveredFlow.emit(it) }
        }

        setContent {
            // Picker screen state: null = showing server list; non-null = showing
            // the window picker for the chosen server.
            var pickerViewModel: WindowPickerViewModel? by remember { mutableStateOf(null) }
            var chosenServer: ServerInformation? by remember { mutableStateOf(null) }
            var connectingTo: ServerInformation? by remember { mutableStateOf(null) }
            var lastConnectionError: String? by remember { mutableStateOf(null) }

            val currentServer: ServerInformation? = chosenServer
            val currentViewModel: WindowPickerViewModel? = pickerViewModel

            if (currentServer == null || currentViewModel == null) {
                ServerPickerScreen(
                    discoveredFlow = discoveredFlow.asSharedFlow(),
                    connectingTo = connectingTo,
                    lastError = lastConnectionError,
                    onServerPicked = { server ->
                        connectingTo = server
                        lastConnectionError = null
                        lifecycleScope.launch {
                            val client = MultiStreamControlClient(
                                host = server.host.hostAddress ?: server.hostname,
                                port = server.controlPort,
                                displayCapabilities = DisplayCapabilities(
                                    maximumWidth = 3840,
                                    maximumHeight = 2160,
                                    supportedCodecs = listOf("h264")
                                )
                            )
                            runCatching {
                                val connection = client.connect(this)
                                pickerConnection = connection
                                val viewModel = WindowPickerViewModel(
                                    initialWindows = connection.serverHello.windows,
                                    incomingMessages = connection.incoming,
                                    scope = this
                                )
                                pickerViewModel = viewModel
                                chosenServer = server
                                // Keep the connection open so push events flow.
                                // It is closed explicitly in launchStreamingActivity
                                // before handing off to PanelSwitcherActivity so the
                                // server's one-viewer rule isn't tripped, and as a
                                // fallback when the activity is destroyed.
                            }.onFailure { throwable ->
                                lastConnectionError = formatConnectionError(server, throwable)
                                chosenServer = null
                                pickerViewModel = null
                            }
                            connectingTo = null
                        }
                    }
                )
            } else {
                WindowPickerScreen(
                    server = currentServer,
                    viewModel = currentViewModel,
                    onConnect = { selectedWindowIds ->
                        launchStreamingActivity(currentServer, selectedWindowIds)
                    }
                )
            }
        }
    }

    private fun launchStreamingActivity(server: ServerInformation, selectedWindowIds: LongArray) {
        // Close the picker's connection BEFORE launching the streaming
        // activity. PanelSwitcherActivity opens its own MultiStreamControlClient
        // and the v2 server rejects a second concurrent viewer with
        // VIEWER_BUSY. We then finish() so the picker leaves the back stack.
        lifecycleScope.launch {
            try {
                pickerConnection?.close()
            } finally {
                pickerConnection = null
                val intent = Intent(this@ServerSelectionActivity, PanelSwitcherActivity::class.java).apply {
                    putExtra("streamHost", server.host.hostAddress)
                    putExtra("streamPort", server.controlPort)
                    putExtra("selectedWindowIds", selectedWindowIds)
                }
                startActivity(intent)
                finish()
            }
        }
    }

    private fun formatConnectionError(server: ServerInformation, throwable: Throwable): String {
        val target = "${server.hostname} (${server.host.hostAddress}:${server.controlPort})"
        val reason = throwable.message?.takeUnless { it.isBlank() } ?: throwable.javaClass.simpleName
        return "Couldn't connect to $target: $reason"
    }

    override fun onDestroy() {
        // Best-effort: if launchStreamingActivity didn't close it (e.g. user
        // backed out without picking), fire-and-forget close on the way out.
        val connection: MultiStreamControlConnection? = pickerConnection
        pickerConnection = null
        if (connection != null) {
            lifecycleScope.launch { runCatching { connection.close() } }
        }
        discoveryClient.stop()
        super.onDestroy()
    }
}
