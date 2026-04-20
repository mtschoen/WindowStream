package com.mtschoen.windowstream.viewer.app.ui

import androidx.compose.runtime.Composable
import com.mtschoen.windowstream.viewer.xr.WindowStreamScene
import com.mtschoen.windowstream.viewer.xr.XrPanelSink

@Composable
fun ConnectedPanelScreen(xrPanelSink: XrPanelSink) {
    WindowStreamScene(panelSink = xrPanelSink)
}
