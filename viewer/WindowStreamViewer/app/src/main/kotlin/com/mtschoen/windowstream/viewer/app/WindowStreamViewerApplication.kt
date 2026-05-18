package com.mtschoen.windowstream.viewer.app

import android.app.Application
import com.mtschoen.windowstream.viewer.observability.FileLoggingTree
import com.mtschoen.windowstream.viewer.observability.InAppBufferTree
import timber.log.Timber
import java.io.File

class WindowStreamViewerApplication : Application() {

    lateinit var inAppBufferTree: InAppBufferTree
        private set

    override fun onCreate() {
        super.onCreate()
        if (Timber.treeCount == 0) {
            Timber.plant(Timber.DebugTree())
            val logsDirectory = File(getExternalFilesDir(null), "logs")
            Timber.plant(FileLoggingTree(directory = logsDirectory))
            inAppBufferTree = InAppBufferTree(replay = 200)
            Timber.plant(inAppBufferTree)
        }
    }
}
