package com.mtschoen.windowstream.viewer.control

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream

class LengthPrefixFramingTest {
    @Test
    fun `encode prepends uint32 big-endian length`() {
        val output = ByteArrayOutputStream()
        LengthPrefixFraming.writeFrame(output, "hi".toByteArray(Charsets.UTF_8))
        val bytes: ByteArray = output.toByteArray()
        assertEquals(0, bytes[0].toInt())
        assertEquals(0, bytes[1].toInt())
        assertEquals(0, bytes[2].toInt())
        assertEquals(2, bytes[3].toInt())
        assertEquals('h'.code.toByte(), bytes[4])
        assertEquals('i'.code.toByte(), bytes[5])
    }

    @Test
    fun `decode round trips encoded frames`() = runTest {
        val output = ByteArrayOutputStream()
        LengthPrefixFraming.writeFrame(output, "alpha".toByteArray())
        LengthPrefixFraming.writeFrame(output, "beta".toByteArray())
        val input = ByteArrayInputStream(output.toByteArray())
        val first: ByteArray = LengthPrefixFraming.readFrame(input) ?: error("no frame")
        val second: ByteArray = LengthPrefixFraming.readFrame(input) ?: error("no frame")
        assertEquals("alpha", String(first, Charsets.UTF_8))
        assertEquals("beta", String(second, Charsets.UTF_8))
    }

    @Test
    fun `decode returns null on clean end of stream`() = runTest {
        val input = ByteArrayInputStream(ByteArray(0))
        assertEquals(null, LengthPrefixFraming.readFrame(input))
    }

    @Test
    fun `decode throws on negative length`() {
        val input = ByteArrayInputStream(byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte(), 0xFF.toByte()))
        assertThrows<MalformedFrameException> {
            LengthPrefixFraming.readFrame(input)
        }
    }

    @Test
    fun `decode throws on length exceeding maximum`() {
        val input = ByteArrayInputStream(byteArrayOf(0x10, 0x00, 0x00, 0x00))
        assertThrows<MalformedFrameException> {
            LengthPrefixFraming.readFrame(input)
        }
    }

    @Test
    fun `decode throws on truncated payload`() {
        val input = ByteArrayInputStream(byteArrayOf(0x00, 0x00, 0x00, 0x05, 'a'.code.toByte(), 'b'.code.toByte()))
        assertThrows<MalformedFrameException> {
            LengthPrefixFraming.readFrame(input)
        }
    }

    @Test
    fun `decode throws when length prefix is truncated after first byte`() {
        val input = ByteArrayInputStream(byteArrayOf(0x01))
        assertThrows<MalformedFrameException> {
            LengthPrefixFraming.readFrame(input)
        }
    }

    @Test
    fun `decode throws when length prefix is truncated after second byte`() {
        val input = ByteArrayInputStream(byteArrayOf(0x00, 0x01))
        assertThrows<MalformedFrameException> {
            LengthPrefixFraming.readFrame(input)
        }
    }

    @Test
    fun `decode throws when length prefix is truncated after third byte`() {
        val input = ByteArrayInputStream(byteArrayOf(0x00, 0x00, 0x01))
        assertThrows<MalformedFrameException> {
            LengthPrefixFraming.readFrame(input)
        }
    }
}
