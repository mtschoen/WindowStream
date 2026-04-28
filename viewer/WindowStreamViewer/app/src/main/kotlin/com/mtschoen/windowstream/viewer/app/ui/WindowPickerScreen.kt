package com.mtschoen.windowstream.viewer.app.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Checkbox
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.mtschoen.windowstream.viewer.control.WindowDescriptor
import com.mtschoen.windowstream.viewer.discovery.ServerInformation

/**
 * Window multi-select screen shown after the user picks a server.
 *
 * Displays the server's advertised window list (from SERVER_HELLO + live
 * WINDOW_ADDED/REMOVED/UPDATED push events via [viewModel]), lets the user
 * tick any number of windows, and enables the Connect button once at least
 * one window is selected.
 *
 * Uses an explicit dark Material3 colour scheme — same rationale as
 * [MultiServerPickerScreen].
 */
@Composable
fun WindowPickerScreen(
    server: ServerInformation,
    viewModel: WindowPickerViewModel,
    onConnect: (selectedWindowIds: LongArray) -> Unit
) {
    val catalogue: Map<ULong, WindowDescriptor> by viewModel.windowCatalogue.collectAsState()
    val selection: Set<ULong> by viewModel.selection.collectAsState()

    val windows: List<WindowDescriptor> = catalogue.values
        .sortedWith(compareBy({ it.processName }, { it.title }))

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
                    text = "Pick windows from ${server.hostname}",
                    fontSize = 16.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(modifier = Modifier.height(24.dp))

                if (windows.isEmpty()) {
                    Text(
                        text = "No windows available on this server.",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        fontSize = 16.sp
                    )
                } else {
                    Column(
                        modifier = Modifier
                            .weight(1f, fill = false)
                            .verticalScroll(rememberScrollState())
                    ) {
                        windows.forEach { window ->
                            val isSelected: Boolean = window.windowId in selection
                            Row(
                                verticalAlignment = Alignment.CenterVertically,
                                modifier = Modifier.padding(vertical = 4.dp)
                            ) {
                                Checkbox(
                                    checked = isSelected,
                                    onCheckedChange = { viewModel.toggleSelection(window.windowId) }
                                )
                                Spacer(modifier = Modifier.padding(horizontal = 4.dp))
                                Column {
                                    Text(
                                        text = window.title.ifBlank { "(untitled)" },
                                        fontSize = 18.sp,
                                        fontWeight = FontWeight.Medium,
                                        color = MaterialTheme.colorScheme.onBackground
                                    )
                                    Text(
                                        text = "${window.processName}  ${window.physicalWidth}×${window.physicalHeight}",
                                        fontSize = 13.sp,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant
                                    )
                                }
                            }
                        }
                    }
                }

                Spacer(modifier = Modifier.height(32.dp))

                Box {
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        FilledTonalButton(
                            onClick = { onConnect(viewModel.selectedWindowIdsAsLongArray()) },
                            enabled = selection.isNotEmpty()
                        ) {
                            Text(
                                text = if (selection.size <= 1) "Connect"
                                else "Connect (${selection.size} windows)",
                                fontSize = 16.sp
                            )
                        }
                        if (selection.isNotEmpty()) {
                            Text(
                                text = "${selection.size} selected",
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                fontSize = 14.sp
                            )
                        }
                    }
                }
            }
        }
    }
}
