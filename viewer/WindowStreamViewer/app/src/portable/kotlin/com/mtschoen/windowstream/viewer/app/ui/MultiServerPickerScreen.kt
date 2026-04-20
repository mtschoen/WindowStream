package com.mtschoen.windowstream.viewer.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import kotlinx.coroutines.flow.SharedFlow

/**
 * Portable-flavor picker that supports selecting one or more discovered
 * servers for simultaneous multi-window viewing. Used by
 * [com.mtschoen.windowstream.viewer.app.ServerSelectionActivity].
 */
@Composable
fun MultiServerPickerScreen(
    discoveredFlow: SharedFlow<ServerInformation>,
    onConnect: (List<ServerInformation>) -> Unit
) {
    val discoveredServers = remember { mutableStateListOf<ServerInformation>() }
    val selectionState = remember { mutableStateMapOf<ServerInformation, Boolean>() }

    LaunchedEffect(Unit) {
        discoveredFlow.collect { server ->
            // Dedupe: NSD can re-resolve the same service. Identity by
            // (host address, controlPort) is enough — a moved server would
            // get a different address and re-appear cleanly.
            val alreadySeen: Boolean = discoveredServers.any { existing ->
                existing.host.hostAddress == server.host.hostAddress &&
                    existing.controlPort == server.controlPort
            }
            if (!alreadySeen) {
                discoveredServers.add(server)
            }
        }
    }

    Column(modifier = Modifier.padding(24.dp)) {
        Text("WindowStream — pick one or more servers")
        Spacer(modifier = Modifier.height(16.dp))

        if (discoveredServers.isEmpty()) {
            Text("Searching…")
        } else {
            discoveredServers.forEach { server ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(
                        checked = selectionState[server] == true,
                        onCheckedChange = { isChecked -> selectionState[server] = isChecked }
                    )
                    Text("${server.hostname} (${server.host.hostAddress}:${server.controlPort})")
                }
            }
        }

        Spacer(modifier = Modifier.height(16.dp))

        val selectedServers: List<ServerInformation> = discoveredServers.filter { selectionState[it] == true }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(
                onClick = { onConnect(selectedServers) },
                enabled = selectedServers.isNotEmpty()
            ) {
                Text(
                    if (selectedServers.size <= 1) "Connect"
                    else "Connect to ${selectedServers.size}"
                )
            }
        }
    }
}
