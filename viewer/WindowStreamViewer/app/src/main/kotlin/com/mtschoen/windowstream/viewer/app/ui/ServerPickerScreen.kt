package com.mtschoen.windowstream.viewer.app.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import kotlinx.coroutines.flow.SharedFlow

@Composable
fun ServerPickerScreen(
    discoveredFlow: SharedFlow<ServerInformation>,
    onServerPicked: (ServerInformation) -> Unit
) {
    val discoveredState by discoveredFlow.collectAsState(initial = null)
    Column(modifier = Modifier.padding(24.dp)) {
        Text("WindowStream — pick a server")
        val currentServer: ServerInformation? = discoveredState
        if (currentServer != null) {
            Button(onClick = { onServerPicked(currentServer) }) {
                Text("${currentServer.hostname} (${currentServer.host.hostAddress}:${currentServer.controlPort})")
            }
        } else {
            Text("Searching…")
        }
    }
}
