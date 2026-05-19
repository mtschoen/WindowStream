package com.mtschoen.windowstream.viewer.observability

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.io.TempDir
import timber.log.Timber
import java.io.File
import java.time.Clock
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

class FileLoggingTreeTest {

    private fun clockAt(text: String): Clock = Clock.fixed(Instant.parse(text), ZoneOffset.UTC)

    private inline fun withPlanted(tree: FileLoggingTree, block: () -> Unit) {
        Timber.plant(tree)
        try { block() } finally {
            Timber.uproot(tree)
            tree.close()
        }
    }

    @Test
    fun `INFO event writes one JSONL line to dated file`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T12:34:56Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.UdpBound(port = 53235))
            tree.flush()
        }
        val expected = File(tempDir, "viewer-2026-05-17.jsonl")
        assertTrue(expected.exists())
        val lines = expected.readLines()
        assertEquals(1, lines.size)
        assertTrue(lines[0].contains("\"eventType\":\"UdpBound\""))
        assertTrue(lines[0].contains("\"level\":\"INFO\""))
    }

    @Test
    fun `WARNING event writes WARN level`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.DiscoveryTimedOut)
            tree.flush()
        }
        val line = File(tempDir, "viewer-2026-05-17.jsonl").readLines().single()
        assertTrue(line.contains("\"level\":\"WARN\""))
        assertTrue(line.contains("\"eventType\":\"DiscoveryTimedOut\""))
    }

    @Test
    fun `ERROR event writes exception field with stack trace`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.TcpConnectFailed(host = "h", port = 1, cause = RuntimeException("refused")))
            tree.flush()
        }
        val line = File(tempDir, "viewer-2026-05-17.jsonl").readLines().single()
        assertTrue(line.contains("\"level\":\"ERROR\""))
        assertTrue(line.contains("\"exception\":"))
        assertTrue(line.contains("refused"))
    }

    @Test
    fun `streamId-bearing event records streamId and extra payload fields`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.StreamOpened(sid = 4, width = 1920, height = 1080))
            tree.flush()
        }
        val line = File(tempDir, "viewer-2026-05-17.jsonl").readLines().single()
        assertTrue(line.contains("\"streamId\":\"4\""))
        assertTrue(line.contains("\"width\":\"1920\""))
        assertTrue(line.contains("\"height\":\"1080\""))
    }

    @Test
    fun `two events same day do not rotate the writer`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            Diagnostics.report(PipelineEvent.UdpBound(port = 2))
            tree.flush()
        }
        val lines = File(tempDir, "viewer-2026-05-17.jsonl").readLines()
        assertEquals(2, lines.size)
    }

    @Test
    fun `rotation deletes files older than retentionDays`(@TempDir tempDir: File) {
        File(tempDir, "viewer-2026-05-09.jsonl").writeText("old\n")
        File(tempDir, "viewer-2026-05-10.jsonl").writeText("old\n")
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            tree.flush()
        }
        assertFalse(File(tempDir, "viewer-2026-05-09.jsonl").exists())
        assertTrue(File(tempDir, "viewer-2026-05-10.jsonl").exists())
    }

    @Test
    fun `malformed filename is ignored during purge`(@TempDir tempDir: File) {
        File(tempDir, "viewer-2026-99-99.jsonl").writeText("bogus\n")
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            tree.flush()
        }
        assertTrue(File(tempDir, "viewer-2026-99-99.jsonl").exists())
        assertTrue(File(tempDir, "viewer-2026-05-17.jsonl").exists())
    }

    @Test
    fun `write failure path is reached without crashing the caller`(@TempDir tempDir: File) {
        val blocker = File(tempDir, "not-a-dir.txt").apply { writeText("file") }
        val tree = FileLoggingTree(directory = blocker, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            tree.flush()
        }
        assertFalse(File(blocker, "viewer-2026-05-17.jsonl").exists())
    }

    @Test
    fun `default constructor uses systemUTC clock and 7-day retention`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir)
        withPlanted(tree) {
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            tree.flush()
        }
        val files = tempDir.listFiles { _, name -> name.matches(Regex("""viewer-\d{4}-\d{2}-\d{2}\.jsonl""")) }!!
        assertEquals(1, files.size)
    }

    @Test
    fun `raw Timber call without Diagnostics context defaults eventType to Log`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Timber.tag("Pipeline").i("plain message")
            tree.flush()
        }
        val line = File(tempDir, "viewer-2026-05-17.jsonl").readLines().single()
        assertTrue(line.contains("\"eventType\":\"Log\""))
        assertTrue(line.contains("\"msg\":\"plain message\""))
    }

    @Test
    fun `null-valued payload key is serialized as empty string`(@TempDir tempDir: File) {
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        withPlanted(tree) {
            Diagnostics.currentPayload.set(mapOf("eventType" to "Synthetic", "extra" to null))
            try {
                Timber.tag("Pipeline").i("test")
            } finally {
                Diagnostics.currentPayload.remove()
            }
            tree.flush()
        }
        val line = File(tempDir, "viewer-2026-05-17.jsonl").readLines().single()
        assertTrue(line.contains("\"extra\":\"\""))
    }

    @Test
    fun `event is durable on disk without an explicit flush or close call`(@TempDir tempDir: File) {
        // Regression for the on-device bug where Application planted the tree but never called
        // close/flush, so BufferedWriter's 8 KB internal buffer held writes in memory until the
        // process died. Production code must persist each event without external help.
        val tree = FileLoggingTree(directory = tempDir, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        try {
            Timber.plant(tree)
            Diagnostics.report(PipelineEvent.UdpBound(port = 1))
            val file = File(tempDir, "viewer-2026-05-17.jsonl")
            val deadline = System.currentTimeMillis() + 2_000
            while (System.currentTimeMillis() < deadline && (!file.exists() || file.length() == 0L)) {
                Thread.sleep(20)
            }
            assertTrue(file.exists(), "Expected log file to be created")
            assertTrue(file.length() > 0L, "Expected per-line flush to make the event visible without explicit flush()")
        } finally {
            Timber.uproot(tree)
            tree.close()
        }
    }

    @Test
    fun `purgeOldFiles tolerates missing directory`(@TempDir tempDir: File) {
        val sub = File(tempDir, "sub")
        val tree = FileLoggingTree(directory = sub, retentionDays = 7, clock = clockAt("2026-05-17T00:00:00Z"))
        try {
            sub.deleteRecursively()
            tree.purgeOldFiles(LocalDate.of(2026, 5, 17))
        } finally {
            tree.close()
        }
    }
}
