package com.mtschoen.windowstream.viewer.control

import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class ProtocolSerializationTest {
    private val json: Json = ProtocolSerialization.json

    private fun roundTrip(message: ControlMessage): ControlMessage {
        val encoded: String = json.encodeToString(ControlMessage.serializer(), message)
        return json.decodeFromString(ControlMessage.serializer(), encoded)
    }

    private val sampleWindow = WindowDescriptor(
        windowId = 42uL,
        hwnd = 0x12345678L,
        processId = 4321,
        processName = "explorer.exe",
        title = "Sample Window",
        physicalWidth = 1920,
        physicalHeight = 1080
    )

    // ─── existing v1 messages — refactored to v2 shapes ───────────────────────

    @Test
    fun `hello round trip preserves viewer version and display capabilities`() {
        val original: ControlMessage = ControlMessage.Hello(
            viewerVersion = 1,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 3840,
                maximumHeight = 2160,
                supportedCodecs = listOf("h264")
            )
        )
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `server hello with windows list round trips`() {
        val original: ControlMessage = ControlMessage.ServerHello(
            serverVersion = 2,
            udpPort = 51000,
            windows = listOf(sampleWindow)
        )
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `server hello with empty windows list round trips`() {
        val original: ControlMessage = ControlMessage.ServerHello(
            serverVersion = 2,
            udpPort = 0,
            windows = emptyList()
        )
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `stream started round trips with windowId`() {
        val original: ControlMessage = ControlMessage.StreamStarted(
            streamId = 7,
            windowId = 42uL,
            codec = "h264",
            width = 2560,
            height = 1440,
            framesPerSecond = 120
        )
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `stream stopped round trips with reason`() {
        val original: ControlMessage = ControlMessage.StreamStopped(
            streamId = 7,
            reason = StreamStoppedReason.WindowGone
        )
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `request keyframe round trips`() {
        val original: ControlMessage = ControlMessage.RequestKeyframe(7)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `heartbeat round trips`() {
        val original: ControlMessage = ControlMessage.Heartbeat
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `error message round trips`() {
        val original: ControlMessage = ControlMessage.ErrorMessage("VIEWER_BUSY", "server already has a connected viewer")
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `viewer ready round trips with only viewerUdpPort`() {
        val original: ControlMessage = ControlMessage.ViewerReady(viewerUdpPort = 51234)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `key event round trips with streamId`() {
        val original: ControlMessage = ControlMessage.KeyEvent(
            streamId = 3,
            keyCode = 0x41,
            isUnicode = true,
            isDown = true
        )
        assertEquals(original, roundTrip(original))
    }

    // ─── new v2 messages ──────────────────────────────────────────────────────

    @Test
    fun `window added round trips`() {
        val original: ControlMessage = ControlMessage.WindowAdded(window = sampleWindow)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `window removed round trips`() {
        val original: ControlMessage = ControlMessage.WindowRemoved(windowId = 42uL)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `window updated round trips with all fields`() {
        val original: ControlMessage = ControlMessage.WindowUpdated(
            windowId = 42uL,
            title = "New Title",
            physicalWidth = 2560,
            physicalHeight = 1440
        )
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `window updated round trips with null optionals`() {
        val original: ControlMessage = ControlMessage.WindowUpdated(
            windowId = 42uL,
            title = null,
            physicalWidth = null,
            physicalHeight = null
        )
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `window snapshot round trips`() {
        val original: ControlMessage = ControlMessage.WindowSnapshot(windows = listOf(sampleWindow))
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `list windows round trips`() {
        val original: ControlMessage = ControlMessage.ListWindows
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `open stream round trips`() {
        val original: ControlMessage = ControlMessage.OpenStream(windowId = 42uL)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `close stream round trips`() {
        val original: ControlMessage = ControlMessage.CloseStream(streamId = 7)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `pause stream round trips`() {
        val original: ControlMessage = ControlMessage.PauseStream(streamId = 7)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `resume stream round trips`() {
        val original: ControlMessage = ControlMessage.ResumeStream(streamId = 7)
        assertEquals(original, roundTrip(original))
    }

    @Test
    fun `focus window round trips`() {
        val original: ControlMessage = ControlMessage.FocusWindow(streamId = 7)
        assertEquals(original, roundTrip(original))
    }

    // ─── enum + descriptor round trips ────────────────────────────────────────

    @Test
    fun `every stream stopped reason round trips`() {
        StreamStoppedReason.entries.forEach { reason ->
            val original: ControlMessage = ControlMessage.StreamStopped(streamId = 1, reason = reason)
            assertEquals(original, roundTrip(original), "reason $reason failed to round-trip")
        }
    }

    @Test
    fun `stream stopped reason wire form is screaming snake case`() {
        val message: ControlMessage = ControlMessage.StreamStopped(streamId = 1, reason = StreamStoppedReason.ClosedByViewer)
        val encoded: String = json.encodeToString(ControlMessage.serializer(), message)
        assertTrue(encoded.contains("\"reason\":\"CLOSED_BY_VIEWER\""), "encoded=$encoded")
    }

    @Test
    fun `window descriptor round trips inside a window snapshot`() {
        val descriptor = WindowDescriptor(
            windowId = ULong.MAX_VALUE,
            hwnd = Long.MAX_VALUE,
            processId = Int.MAX_VALUE,
            processName = "test.exe",
            title = "max-bounds",
            physicalWidth = 7680,
            physicalHeight = 4320
        )
        val original: ControlMessage = ControlMessage.WindowSnapshot(windows = listOf(descriptor))
        assertEquals(original, roundTrip(original))
    }

    // ─── discriminator ────────────────────────────────────────────────────────

    @Test
    fun `type discriminator uses type field with serial names`() {
        val encoded: String = json.encodeToString(ControlMessage.serializer(), ControlMessage.Heartbeat)
        assertEquals("""{"type":"HEARTBEAT"}""", encoded)
    }

    @Test
    fun `new message types use screaming snake case discriminators`() {
        val expectedDiscriminators = mapOf(
            ControlMessage.ListWindows to "LIST_WINDOWS",
            ControlMessage.OpenStream(windowId = 1uL) to "OPEN_STREAM",
            ControlMessage.CloseStream(streamId = 1) to "CLOSE_STREAM",
            ControlMessage.PauseStream(streamId = 1) to "PAUSE_STREAM",
            ControlMessage.ResumeStream(streamId = 1) to "RESUME_STREAM",
            ControlMessage.FocusWindow(streamId = 1) to "FOCUS_WINDOW",
            ControlMessage.WindowAdded(window = sampleWindow) to "WINDOW_ADDED",
            ControlMessage.WindowRemoved(windowId = 1uL) to "WINDOW_REMOVED",
            ControlMessage.WindowSnapshot(windows = listOf(sampleWindow)) to "WINDOW_SNAPSHOT"
        )
        for ((message, expected) in expectedDiscriminators) {
            val encoded: String = json.encodeToString(ControlMessage.serializer(), message)
            assertTrue(encoded.contains("\"type\":\"$expected\""), "expected $expected in $encoded")
        }
    }
}
