package com.mtschoen.windowstream.viewer.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import kotlinx.coroutines.flow.SharedFlow

/**
 * Portable-flavor picker that supports selecting one or more discovered
 * servers for simultaneous multi-window viewing. Used by
 * [com.mtschoen.windowstream.viewer.app.ServerSelectionActivity].
 *
 * Uses an explicit dark Material3 color scheme so Compose components don't
 * fall back to the baseline (light) scheme on top of the Activity's dark
 * Theme.Material.NoActionBar window — that mismatch produced an unreadable
 * low-contrast picker on Quest and phone.
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

    val selectedServers: List<ServerInformation> = discoveredServers.filter { selectionState[it] == true }

    MaterialTheme(colorScheme = darkColorScheme()) {
        Surface(
            modifier = Modifier.fillMaxSize(),
            color = MaterialTheme.colorScheme.background
        ) {
            Column(modifier = Modifier.padding(horizontal = 32.dp, vertical = 40.dp)) {
                Text(
                    text = "WindowStream",
                    fontSize = 32.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.onBackground
                )
                Text(
                    text = "Pick one or more servers to view.",
                    fontSize = 16.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(modifier = Modifier.height(24.dp))

                if (discoveredServers.isEmpty()) {
                    Text(
                        text = "Searching the LAN for servers…",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        fontSize = 16.sp
                    )
                } else {
                    discoveredServers.forEach { server ->
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier.padding(vertical = 4.dp)
                        ) {
                            Checkbox(
                                checked = selectionState[server] == true,
                                onCheckedChange = { isChecked -> selectionState[server] = isChecked }
                            )
                            Spacer(modifier = Modifier.padding(horizontal = 4.dp))
                            Column {
                                Text(
                                    text = server.hostname,
                                    fontSize = 18.sp,
                                    fontWeight = FontWeight.Medium,
                                    color = MaterialTheme.colorScheme.onBackground
                                )
                                Text(
                                    text = "${server.host.hostAddress}:${server.controlPort}",
                                    fontSize = 13.sp,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                    }
                }

                Spacer(modifier = Modifier.height(32.dp))

                Box(modifier = Modifier.fillMaxSize()) {
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        modifier = Modifier.align(Alignment.TopStart)
                    ) {
                        FilledTonalButton(
                            onClick = { onConnect(selectedServers) },
                            enabled = selectedServers.isNotEmpty()
                        ) {
                            Text(
                                text = if (selectedServers.size <= 1) "Connect"
                                else "Connect to ${selectedServers.size}",
                                fontSize = 16.sp
                            )
                        }
                        if (selectedServers.isNotEmpty()) {
                            Text(
                                text = "${selectedServers.size} selected",
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                fontSize = 14.sp,
                                modifier = Modifier.align(Alignment.CenterVertically)
                            )
                        }
                    }
                }
            }
        }
    }
}
