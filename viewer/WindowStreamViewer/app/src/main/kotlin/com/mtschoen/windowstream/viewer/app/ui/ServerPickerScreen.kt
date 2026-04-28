package com.mtschoen.windowstream.viewer.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import kotlinx.coroutines.flow.SharedFlow

/**
 * Server discovery list shown on first launch. Displays every server discovered
 * by mDNS via [discoveredFlow] as a clickable card. Tapping a card immediately
 * calls [onServerPicked] — this is a single-select screen; multi-select has
 * moved to [WindowPickerScreen].
 *
 * Uses an explicit dark Material3 colour scheme so Compose components don't
 * fall back to the baseline (light) scheme on top of the Activity's dark
 * Theme.Material.NoActionBar window.
 */
@Composable
fun ServerPickerScreen(
    discoveredFlow: SharedFlow<ServerInformation>,
    onServerPicked: (ServerInformation) -> Unit
) {
    val discoveredServers = remember { mutableStateListOf<ServerInformation>() }

    LaunchedEffect(Unit) {
        discoveredFlow.collect { server ->
            // Dedupe: NSD can re-resolve the same service. Identity by
            // (host address, controlPort) is enough — a moved server gets a
            // different address and re-appears cleanly.
            val alreadySeen: Boolean = discoveredServers.any { existing ->
                existing.host.hostAddress == server.host.hostAddress &&
                    existing.controlPort == server.controlPort
            }
            if (!alreadySeen) {
                discoveredServers.add(server)
            }
        }
    }

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
                    text = "Tap a server to pick its windows.",
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
                    Column(modifier = Modifier.verticalScroll(rememberScrollState())) {
                        discoveredServers.forEach { server ->
                            Card(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(vertical = 6.dp)
                                    .clickable { onServerPicked(server) },
                                colors = CardDefaults.cardColors(
                                    containerColor = MaterialTheme.colorScheme.surfaceVariant
                                )
                            ) {
                                Column(modifier = Modifier.padding(16.dp)) {
                                    Text(
                                        text = server.hostname,
                                        fontSize = 18.sp,
                                        fontWeight = FontWeight.Medium,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
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
                }
            }
        }
    }
}
