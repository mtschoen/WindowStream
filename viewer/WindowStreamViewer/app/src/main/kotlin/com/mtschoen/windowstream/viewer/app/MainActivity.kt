package com.mtschoen.windowstream.viewer.app

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.lifecycleScope
import com.mtschoen.windowstream.viewer.app.ui.ConnectedPanelScreen
import com.mtschoen.windowstream.viewer.app.ui.ServerPickerScreen
import com.mtschoen.windowstream.viewer.discovery.NetworkServiceDiscoveryClient
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.state.ViewerEvent
import com.mtschoen.windowstream.viewer.xr.XrPanelSink
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private lateinit var pipeline: ViewerPipeline
    private lateinit var xrPanelSink: XrPanelSink

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        xrPanelSink = XrPanelSink()
        pipeline = ViewerPipeline.create(frameSink = xrPanelSink)
        val discoveryClient = NetworkServiceDiscoveryClient(applicationContext)
        val discoveredFlow: MutableSharedFlow<ServerInformation> = MutableSharedFlow(
            replay = 1, extraBufferCapacity = 16, onBufferOverflow = BufferOverflow.DROP_OLDEST
        )

        lifecycleScope.launch {
            pipeline.stateMachine.reduce(ViewerEvent.StartDiscovery)
            discoveryClient.discover(this).collect { discoveredFlow.emit(it) }
        }

        setContent {
            var selectedServer: ServerInformation? by remember { mutableStateOf(null) }
            if (selectedServer == null) {
                ServerPickerScreen(
                    discoveredFlow = discoveredFlow.asSharedFlow(),
                    onServerPicked = { server ->
                        selectedServer = server
                        lifecycleScope.launch { pipeline.connect(lifecycleScope, server) }
                    }
                )
            } else {
                ConnectedPanelScreen(xrPanelSink = xrPanelSink)
            }
        }
    }

    override fun onDestroy() {
        pipeline.disconnect()
        super.onDestroy()
    }
}
