package com.mtschoen.windowstream.viewer.app

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.lifecycle.lifecycleScope
import com.mtschoen.windowstream.viewer.app.ui.MultiServerPickerScreen
import com.mtschoen.windowstream.viewer.demo.DemoActivity
import com.mtschoen.windowstream.viewer.discovery.NetworkServiceDiscoveryClient
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch

/**
 * Launcher activity for the portable flavor. Runs mDNS discovery, shows the
 * multi-select server picker, and on selection hands off to DemoActivity
 * which renders N streams onto a grid of SurfaceViews. Has no Jetpack XR
 * dependency so it runs on Quest, phones, tablets, and Galaxy XR as a 2D
 * window.
 */
class ServerSelectionActivity : ComponentActivity() {
    private lateinit var discoveryClient: NetworkServiceDiscoveryClient

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
            MultiServerPickerScreen(
                discoveredFlow = discoveredFlow.asSharedFlow(),
                onConnect = { servers -> launchStreamingActivity(servers) }
            )
        }
    }

    private fun launchStreamingActivity(servers: List<ServerInformation>) {
        val intent = Intent(this, DemoActivity::class.java).apply {
            putExtra(
                "streamHosts",
                servers.map { it.host.hostAddress }.toTypedArray()
            )
            putExtra(
                "streamPorts",
                servers.map { it.controlPort }.toIntArray()
            )
        }
        startActivity(intent)
    }

    override fun onDestroy() {
        discoveryClient.stop()
        super.onDestroy()
    }
}
