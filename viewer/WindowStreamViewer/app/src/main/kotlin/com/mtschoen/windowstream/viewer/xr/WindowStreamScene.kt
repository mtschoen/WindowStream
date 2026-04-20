package com.mtschoen.windowstream.viewer.xr

import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.unit.dp
import androidx.xr.compose.spatial.Subspace
import androidx.xr.compose.subspace.SpatialExternalSurface
import androidx.xr.compose.subspace.StereoMode
import androidx.xr.compose.subspace.layout.SubspaceModifier
import androidx.xr.compose.subspace.layout.height
import androidx.xr.compose.subspace.layout.movable
import androidx.xr.compose.subspace.layout.offset
import androidx.xr.compose.subspace.layout.width

@Composable
fun WindowStreamScene(panelSink: XrPanelSink) {
    val dimensions by panelSink.dimensions.collectAsState()
    val (panelWidthMeters, panelHeightMeters) = computePanelDimensionsMeters(dimensions)

    Subspace {
        SpatialExternalSurface(
            stereoMode = StereoMode.Mono,
            modifier = SubspaceModifier
                .width(panelWidthMeters.dp)
                .height(panelHeightMeters.dp)
                .offset(z = (-PanelPlacement.DEFAULT_DISTANCE_METERS).dp)
                .movable()
        ) {
            // alpha04 API: register lifecycle callbacks via scope DSL
            onSurfaceCreated { providedSurface ->
                panelSink.provideSurfaceFromXrSystem(providedSurface)
            }
            onSurfaceDestroyed { /* panel teardown — XrPanelSink handles cleanup */ }
        }
    }
}

/**
 * Computes the panel width and height in meters from the decoded-stream pixel dimensions.
 *
 * The panel is always [PanelPlacement.DEFAULT_WIDTH_METERS] wide; height is derived
 * from the aspect ratio so the image is never stretched. When [dimensions] is null
 * (no stream active yet) the function falls back to a 1920×1080 aspect ratio.
 */
internal fun computePanelDimensionsMeters(dimensions: Pair<Int, Int>?): Pair<Float, Float> {
    val width: Int = dimensions?.first ?: 1920
    val height: Int = dimensions?.second ?: 1080
    val aspectRatio: Float = width.toFloat() / height.toFloat()
    val widthMeters: Float = PanelPlacement.DEFAULT_WIDTH_METERS
    val heightMeters: Float = widthMeters / aspectRatio
    return widthMeters to heightMeters
}
