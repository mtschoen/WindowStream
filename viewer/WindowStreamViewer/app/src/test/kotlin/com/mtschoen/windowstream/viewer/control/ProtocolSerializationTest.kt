package com.mtschoen.windowstream.viewer.control

import kotlinx.serialization.json.Json
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class ProtocolSerializationTest {
    private val json: Json = ProtocolSerialization.json

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
        val encoded: String = json.encodeToString(ControlMessage.serializer(), original)
        val decoded: ControlMessage = json.decodeFromString(ControlMessage.serializer(), encoded)
        assertEquals(original, decoded)
    }

    @Test
    fun `server hello with active stream round trips`() {
        val original: ControlMessage = ControlMessage.ServerHello(
            serverVersion = 1,
            activeStream = ActiveStreamDescriptor(
                streamId = 42,
                udpPort = 51000,
                codec = "h264",
                width = 1920,
                height = 1080,
                framesPerSecond = 60
            )
        )
        assertEquals(original, json.decodeFromString(ControlMessage.serializer(), json.encodeToString(ControlMessage.serializer(), original)))
    }

    @Test
    fun `server hello without active stream round trips`() {
        val original: ControlMessage = ControlMessage.ServerHello(serverVersion = 1, activeStream = null)
        assertEquals(original, json.decodeFromString(ControlMessage.serializer(), json.encodeToString(ControlMessage.serializer(), original)))
    }

    @Test
    fun `stream started round trips`() {
        val original: ControlMessage = ControlMessage.StreamStarted(7, 51001, "h264", 2560, 1440, 120)
        assertEquals(original, json.decodeFromString(ControlMessage.serializer(), json.encodeToString(ControlMessage.serializer(), original)))
    }

    @Test
    fun `stream stopped round trips`() {
        val original: ControlMessage = ControlMessage.StreamStopped(7)
        assertEquals(original, json.decodeFromString(ControlMessage.serializer(), json.encodeToString(ControlMessage.serializer(), original)))
    }

    @Test
    fun `request keyframe round trips`() {
        val original: ControlMessage = ControlMessage.RequestKeyframe(7)
        assertEquals(original, json.decodeFromString(ControlMessage.serializer(), json.encodeToString(ControlMessage.serializer(), original)))
    }

    @Test
    fun `heartbeat round trips`() {
        val original: ControlMessage = ControlMessage.Heartbeat
        assertEquals(original, json.decodeFromString(ControlMessage.serializer(), json.encodeToString(ControlMessage.serializer(), original)))
    }

    @Test
    fun `error message round trips`() {
        val original: ControlMessage = ControlMessage.ErrorMessage("VIEWER_BUSY", "server already has a connected viewer")
        assertEquals(original, json.decodeFromString(ControlMessage.serializer(), json.encodeToString(ControlMessage.serializer(), original)))
    }

    @Test
    fun `type discriminator uses type field with serial names`() {
        val encoded: String = json.encodeToString(ControlMessage.serializer(), ControlMessage.Heartbeat)
        assertEquals("""{"type":"HEARTBEAT"}""", encoded)
    }
}
