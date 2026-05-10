package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class AwaitControlMessageTest {

    @Test
    fun `returns matching message when target type arrives first`() = runBlocking {
        val target = ControlMessage.ServerHello(
            serverVersion = 2,
            udpPort = 0,
            windows = emptyList()
        )
        val flow = flowOf<ControlMessage>(target)

        val result: ControlMessage.ServerHello = flow.awaitOrError(ControlMessage.ServerHello::class)

        assertSame(target, result)
    }

    @Test
    fun `skips non-target non-error messages until target arrives`() = runBlocking {
        val target = ControlMessage.StreamStarted(
            streamId = 1,
            windowId = 0u,
            codec = "h264",
            width = 1920,
            height = 1080,
            framesPerSecond = 60
        )
        val flow = flowOf<ControlMessage>(
            ControlMessage.Heartbeat,
            ControlMessage.Heartbeat,
            target
        )

        val result: ControlMessage.StreamStarted = flow.awaitOrError(ControlMessage.StreamStarted::class)

        assertSame(target, result)
    }

    @Test
    fun `throws ControlServerErrorException when error arrives before target`() {
        val errorMessage = ControlMessage.ErrorMessage(
            code = "WINDOW_NOT_FOUND",
            message = "stale hwnd"
        )
        val flow = flowOf<ControlMessage>(errorMessage)

        val thrown = assertThrows(ControlServerErrorException::class.java) {
            runBlocking { flow.awaitOrError(ControlMessage.StreamStarted::class) }
        }

        assertSame(errorMessage, thrown.errorMessage)
        assertEquals("WINDOW_NOT_FOUND", thrown.errorMessage.code)
        val text: String = thrown.message ?: ""
        assertTrue(text.contains("WINDOW_NOT_FOUND"), "exception text was: $text")
        assertTrue(text.contains("stale hwnd"), "exception text was: $text")
    }

    @Test
    fun `surfaces error even when interleaved with non-target messages`() {
        val errorMessage = ControlMessage.ErrorMessage(
            code = "CAPACITY_EXCEEDED",
            message = "too many viewers"
        )
        val flow = flowOf<ControlMessage>(
            ControlMessage.Heartbeat,
            errorMessage,
            ControlMessage.StreamStarted(
                streamId = 1,
                windowId = 0u,
                codec = "h264",
                width = 1920,
                height = 1080,
                framesPerSecond = 60
            )
        )

        val thrown = assertThrows(ControlServerErrorException::class.java) {
            runBlocking { flow.awaitOrError(ControlMessage.StreamStarted::class) }
        }

        assertEquals("CAPACITY_EXCEEDED", thrown.errorMessage.code)
    }
}
