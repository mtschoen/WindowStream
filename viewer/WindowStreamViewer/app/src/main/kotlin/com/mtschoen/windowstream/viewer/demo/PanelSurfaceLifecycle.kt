package com.mtschoen.windowstream.viewer.demo

import android.os.Build
import android.view.SurfaceView

/** Uses attachment lifecycle on Android 14+, leaving older platform defaults unchanged. */
internal fun retainPanelSurfaceWhileAttached(
    surfaceView: SurfaceView,
    androidApiLevel: Int,
) {
    if (androidApiLevel < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
        return
    }

    surfaceView.setSurfaceLifecycle(SurfaceView.SURFACE_LIFECYCLE_FOLLOWS_ATTACHMENT)
}
