package com.mtschoen.windowstream.viewer.observability

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import timber.log.Timber
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.io.StringWriter
import java.time.Clock
import java.time.LocalDate
import java.time.ZoneOffset
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

class FileLoggingTree(
    private val directory: File,
    private val retentionDays: Int = 7,
    private val clock: Clock = Clock.systemUTC(),
    private val enablePerWriteFlush: Boolean = true,
) : Timber.Tree(), AutoCloseable {

    private val executor: ExecutorService = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "WindowStream-Log-Writer").apply { isDaemon = true }
    }
    private var currentDate: LocalDate? = null
    // Sentinel writer keeps the field non-nullable, avoiding a dead-branch null
    // check at the appendLine call site after a successful rotateIfNeeded.
    private var writer: BufferedWriter = BufferedWriter(StringWriter())

    init {
        directory.mkdirs()
    }

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        val payload: Map<String, Any?> = Diagnostics.currentPayload.get()
        val severity = when {
            priority >= android.util.Log.ERROR -> "ERROR"
            priority >= android.util.Log.WARN -> "WARN"
            else -> "INFO"
        }
        val nowInstant = clock.instant()
        val nowDate = LocalDate.ofInstant(nowInstant, ZoneOffset.UTC)

        val record = buildJsonObject {
            put("ts", nowInstant.toString())
            put("level", severity)
            put("eventType", (payload["eventType"] as? String) ?: "Log")
            payload["streamId"]?.let { put("streamId", it.toString()) }
            put("msg", message)
            t?.let { put("exception", it.stackTraceToString()) }
            for ((key, value) in payload) {
                if (key == "eventType" || key == "streamId") continue
                put(key, (value ?: "").toString())
            }
        }
        val line = Json.encodeToString(JsonElement.serializer(), record)

        executor.execute {
            try {
                rotateIfNeeded(nowDate)
                writer.appendLine(line)
                // Per-line flush: pipeline events are low-rate (human-ui scale) and process death
                // shouldn't lose the very logs we'd want for postmortem. The 8 KB BufferedWriter
                // default would otherwise hold writes in memory until close, which never gets
                // called on Android lifecycle teardown.
                if (enablePerWriteFlush) {
                    writer.flush()
                }
            } catch (failure: Throwable) {
                System.err.println("FileLoggingTree: write failed: ${failure.message}")
                failure.printStackTrace(System.err)
            }
        }
    }

    fun flush() {
        executor.submit { writer.flush() }.get()
    }

    private fun rotateIfNeeded(today: LocalDate) {
        if (currentDate == today) return
        val file = File(directory, "viewer-$today.jsonl")
        val newWriter = BufferedWriter(FileWriter(file, /* append = */ true))
        writer.close()
        writer = newWriter
        currentDate = today
        purgeOldFiles(today)
    }

    internal fun purgeOldFiles(today: LocalDate) {
        val cutoff = today.minusDays(retentionDays.toLong())
        directory.listFiles { _, name -> name.matches(Regex("""viewer-\d{4}-\d{2}-\d{2}\.jsonl""")) }
            ?.forEach { file ->
                val dateText = file.nameWithoutExtension.removePrefix("viewer-")
                val fileDate = runCatching { LocalDate.parse(dateText) }.getOrNull() ?: return@forEach
                if (fileDate.isBefore(cutoff)) file.delete()
            }
    }

    override fun close() {
        executor.submit { writer.close() }.get()
        executor.shutdown()
    }
}
