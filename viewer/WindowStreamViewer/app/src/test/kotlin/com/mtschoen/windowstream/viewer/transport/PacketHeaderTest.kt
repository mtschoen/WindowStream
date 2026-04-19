package com.mtschoen.windowstream.viewer.transport

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class PacketHeaderTest {
    @Test
    fun `parse extracts every field`() {
        val packet: ByteArray = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,                          // magic 'WSTR'
            0x00, 0x00, 0x00, 0x07,                          // streamId = 7
            0x00, 0x00, 0x01, 0x2C,                          // sequence = 300
            0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x42, 0x40,  // pts = 1_000_000 microseconds
            0x03,                                            // flags = IDR(1) | LAST_FRAGMENT(2)
            0x02,                                            // fragmentIndex = 2
            0x05,                                            // fragmentTotal = 5
            0x00,                                            // reserved
            0xAA.toByte(), 0xBB.toByte()                     // two payload bytes
        )
        val parsed: PacketHeader = PacketHeader.parse(packet, packet.size)
        assertEquals(7, parsed.streamId)
        assertEquals(300L, parsed.sequence)
        assertEquals(1_000_000L, parsed.presentationTimestampMicroseconds)
        assertTrue(parsed.isIdrFrame)
        assertTrue(parsed.isLastFragment)
        assertEquals(2, parsed.fragmentIndex)
        assertEquals(5, parsed.fragmentTotal)
        assertEquals(2, parsed.payloadLength)
        assertEquals(0xAA.toByte(), parsed.payloadAt(0))
        assertEquals(0xBB.toByte(), parsed.payloadAt(1))
    }

    @Test
    fun `parse rejects bad magic`() {
        val packet = ByteArray(24) { 0 }
        assertThrows<MalformedPacketException> {
            PacketHeader.parse(packet, packet.size)
        }
    }

    @Test
    fun `parse rejects packets shorter than header`() {
        val packet = ByteArray(23) { 0 }
        assertThrows<MalformedPacketException> {
            PacketHeader.parse(packet, packet.size)
        }
    }

    @Test
    fun `parse rejects fragmentIndex at or above fragmentTotal`() {
        val packet: ByteArray = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00,
            0x05.toByte(),
            0x05.toByte(),
            0x00
        )
        assertThrows<MalformedPacketException> {
            PacketHeader.parse(packet, packet.size)
        }
    }
}
