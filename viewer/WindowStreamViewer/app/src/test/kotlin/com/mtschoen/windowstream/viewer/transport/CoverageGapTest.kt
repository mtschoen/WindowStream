package com.mtschoen.windowstream.viewer.transport

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

/**
 * Fills the last coverage gaps discovered after merging Round 1b:
 * - [FragmentReassembler]'s no-argument constructor (default parameters)
 * - [PacketHeader.flags] getter read directly
 * - [PacketHeader.isLastFragment] false branch
 */
class CoverageGapTest {
    @Test
    fun `fragment reassembler default constructor exercises default parameters`() {
        val reassembler = FragmentReassembler()
        val packet: ByteArray = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02,
            0x00,
            0x01,
            0x00,
            0x11
        )
        val header = PacketHeader.parse(packet, packet.size)
        assertNotNull(header)
        val frame = reassembler.offer(header!!)
        assertNotNull(frame)
        assertEquals(1, frame!!.payload.size)
    }

    @Test
    fun `packet header flags getter and isLastFragment false branch`() {
        val packet: ByteArray = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00,
            0x00,
            0x02,
            0x00,
            0xAA.toByte()
        )
        val header = PacketHeader.parse(packet, packet.size)
        assertNotNull(header)
        assertEquals(0, header!!.flags)
        assertFalse(header.isLastFragment)
    }

    @Test
    fun `fragment reassembler with default clock does not evict a fresh partial`() {
        val reassembler = FragmentReassembler()
        val first: ByteArray = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00,
            0x00,
            0x02,
            0x00,
            0xAA.toByte()
        )
        val firstHeader = PacketHeader.parse(first, first.size)
        assertNotNull(firstHeader)
        assertNull(reassembler.offer(firstHeader!!))
    }
}
