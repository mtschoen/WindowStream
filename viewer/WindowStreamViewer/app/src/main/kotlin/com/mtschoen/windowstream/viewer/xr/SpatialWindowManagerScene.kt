package com.mtschoen.windowstream.viewer.xr

import android.view.Surface
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface as Material3Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.xr.compose.spatial.Subspace
import androidx.xr.compose.subspace.SpatialColumn
import androidx.xr.compose.subspace.SpatialExternalSurface
import androidx.xr.compose.subspace.SpatialPanel
import androidx.xr.compose.subspace.StereoMode
import androidx.xr.compose.subspace.layout.SubspaceModifier
import androidx.xr.compose.subspace.layout.height
import androidx.xr.compose.subspace.layout.movable
import androidx.xr.compose.subspace.layout.offset
import androidx.xr.compose.subspace.layout.width
import com.mtschoen.windowstream.viewer.control.WindowDescriptor

/**
 * The spatial window manager scene for the gxr (Android XR) flavor.
 *
 * Renders, inside a single [Subspace]:
 * - a persistent **drawer** panel listing the server's window catalogue; tapping
 *   a window spawns it (or is shown as already-open),
 * - one **window panel** per open stream — a movable [SpatialColumn] holding a
 *   2D chrome bar ([SpatialPanel], which captures input) above the video
 *   ([SpatialExternalSurface], which does not).
 *
 * All interaction is delegated upward via the callbacks; the Activity owns the
 * per-window runtime resources (decoder / sink / UDP) and the
 * [SpatialWindowManager] state. This composable is intentionally logic-free and
 * is excluded from coverage like the rest of the Compose-for-XR layer.
 *
 * Coordinate convention: the existing panels treat 1 meter as 1000 dp in
 * subspace units (`meters * 1000f`); reused here for offsets and sizes.
 */
@Composable
fun SpatialWindowManagerScene(
    windowCatalogue: Map<ULong, WindowDescriptor>,
    panels: List<SpatialPanelState>,
    onSpawn: (ULong) -> Unit,
    onClose: (ULong) -> Unit,
    onToggleMinimize: (ULong) -> Unit,
    onAdjustScale: (ULong, Float) -> Unit,
    onSurfaceProvided: (ULong, Surface) -> Unit,
    onSurfaceDestroyed: (ULong) -> Unit,
) {
    val openWindowIds: Set<ULong> = panels.mapTo(mutableSetOf()) { it.windowId }

    Subspace {
        // Drawer: fixed to the left of the panel fan.
        SpatialPanel(
            modifier = SubspaceModifier
                .width(DRAWER_WIDTH_DP.dp)
                .height(DRAWER_HEIGHT_DP.dp)
                .offset(
                    x = (DRAWER_OFFSET_METERS * METERS_TO_DP).dp,
                    z = (-PanelPlacement.DEFAULT_DISTANCE_METERS * METERS_TO_DP).dp,
                )
                .movable()
        ) {
            WindowDrawerContent(
                windowCatalogue = windowCatalogue,
                openWindowIds = openWindowIds,
                onSpawn = onSpawn,
                onClose = onClose,
            )
        }

        panels.forEachIndexed { index, panel ->
            val slotXMeters: Float = SpatialPanelLayout.computeSlotOffsetMeters(index, panels.size)
            WindowPanelColumn(
                panel = panel,
                slotXMeters = slotXMeters,
                onClose = onClose,
                onToggleMinimize = onToggleMinimize,
                onAdjustScale = onAdjustScale,
                onSurfaceProvided = onSurfaceProvided,
                onSurfaceDestroyed = onSurfaceDestroyed,
            )
        }
    }
}

@Composable
private fun WindowPanelColumn(
    panel: SpatialPanelState,
    slotXMeters: Float,
    onClose: (ULong) -> Unit,
    onToggleMinimize: (ULong) -> Unit,
    onAdjustScale: (ULong, Float) -> Unit,
    onSurfaceProvided: (ULong, Surface) -> Unit,
    onSurfaceDestroyed: (ULong) -> Unit,
) {
    val (panelWidthMeters, panelHeightMeters) = computePanelDimensionsMeters(
        panel.contentWidthPixels to panel.contentHeightPixels
    )
    val surfaceWidthDp: Float
    val surfaceHeightDp: Float
    if (panel.minimized) {
        // Keep the surface mounted (no decoder/surface-lifecycle churn) but tiny;
        // the stream is paused upstream so the frozen last frame costs no bandwidth.
        surfaceWidthDp = MINIMIZED_WIDTH_METERS * METERS_TO_DP
        surfaceHeightDp = (MINIMIZED_WIDTH_METERS / (panelWidthMeters / panelHeightMeters)) * METERS_TO_DP
    } else {
        surfaceWidthDp = panelWidthMeters * panel.scale * METERS_TO_DP
        surfaceHeightDp = panelHeightMeters * panel.scale * METERS_TO_DP
    }

    SpatialColumn(
        modifier = SubspaceModifier
            .offset(
                x = (slotXMeters * METERS_TO_DP).dp,
                z = (-PanelPlacement.DEFAULT_DISTANCE_METERS * METERS_TO_DP).dp,
            )
            .movable()
    ) {
        SpatialPanel(
            modifier = SubspaceModifier
                .width(surfaceWidthDp.dp.coerceAtLeast(CHROME_MIN_WIDTH_DP.dp))
                .height(CHROME_HEIGHT_DP.dp)
        ) {
            WindowChromeBar(
                panel = panel,
                onClose = onClose,
                onToggleMinimize = onToggleMinimize,
                onAdjustScale = onAdjustScale,
            )
        }

        SpatialExternalSurface(
            stereoMode = StereoMode.Mono,
            modifier = SubspaceModifier
                .width(surfaceWidthDp.dp)
                .height(surfaceHeightDp.dp)
        ) {
            onSurfaceCreated { providedSurface ->
                onSurfaceProvided(panel.windowId, providedSurface)
            }
            onSurfaceDestroyed {
                onSurfaceDestroyed(panel.windowId)
            }
        }
    }
}

// ─── 2D content (rendered inside spatial panels) ──────────────────────────────

@Composable
private fun WindowDrawerContent(
    windowCatalogue: Map<ULong, WindowDescriptor>,
    openWindowIds: Set<ULong>,
    onSpawn: (ULong) -> Unit,
    onClose: (ULong) -> Unit,
) {
    Material3Surface(color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text(
                text = "Windows",
                fontSize = 22.sp,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onSurface,
            )
            if (windowCatalogue.isEmpty()) {
                Text(
                    text = "No windows available",
                    fontSize = 15.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            for ((windowId, descriptor) in windowCatalogue) {
                val isOpen: Boolean = windowId in openWindowIds
                val label: String = descriptor.title.ifBlank { descriptor.processName }
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = label,
                        fontSize = 15.sp,
                        color = MaterialTheme.colorScheme.onSurface,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        // weight(1f) lets the title take remaining space and ellipsize
                        // instead of pushing the Open/Close button off the row.
                        modifier = Modifier.weight(1f),
                    )
                    if (isOpen) {
                        FilledTonalButton(onClick = { onClose(windowId) }) {
                            Text("Close")
                        }
                    } else {
                        Button(onClick = { onSpawn(windowId) }) {
                            Text("Open")
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun WindowChromeBar(
    panel: SpatialPanelState,
    onClose: (ULong) -> Unit,
    onToggleMinimize: (ULong) -> Unit,
    onAdjustScale: (ULong, Float) -> Unit,
) {
    Material3Surface(color = MaterialTheme.colorScheme.surfaceVariant) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 12.dp, vertical = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = panel.title,
                fontSize = 15.sp,
                fontWeight = FontWeight.Medium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                // weight(1f) keeps the chrome buttons (–/+/minimize/×) on-row even
                // when the window title is long.
                modifier = Modifier.weight(1f),
            )
            if (!panel.minimized) {
                FilledTonalButton(onClick = { onAdjustScale(panel.windowId, -SpatialPanelLayout.SCALE_STEP) }) {
                    Text("–")
                }
                FilledTonalButton(onClick = { onAdjustScale(panel.windowId, SpatialPanelLayout.SCALE_STEP) }) {
                    Text("+")
                }
            }
            FilledTonalButton(onClick = { onToggleMinimize(panel.windowId) }) {
                Text(if (panel.minimized) "Restore" else "Minimize")
            }
            Button(onClick = { onClose(panel.windowId) }) {
                Text("×")
            }
        }
    }
}

private const val METERS_TO_DP: Float = 1000f
private const val DRAWER_WIDTH_DP: Float = 360f
private const val DRAWER_HEIGHT_DP: Float = 640f
private const val DRAWER_OFFSET_METERS: Float = -1.4f
private const val CHROME_HEIGHT_DP: Float = 90f
private const val CHROME_MIN_WIDTH_DP: Float = 320f
private const val MINIMIZED_WIDTH_METERS: Float = 0.3f
