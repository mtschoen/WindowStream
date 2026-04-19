package com.mtschoen.windowstream.viewer.transport

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

class FragmentReassemblerTest {
    private fun makePacket(
        streamId: Int,
        sequence: Long,
        presentationTimestampMicroseconds: Long,
        flags: Int,
        fragmentIndex: Int,
        fragmentTotal: Int,
        payload: ByteArray
    ): ByteArray {
        val buffer = ByteArray(PacketHeader.HEADER_BYTE_LENGTH + payload.size)
        buffer[0] = 0x57; buffer[1] = 0x53; buffer[2] = 0x54; buffer[3] = 0x52
        writeInt(buffer, 4, streamId)
        writeInt(buffer, 8, sequence.toInt())
        writeLong(buffer, 12, presentationTimestampMicroseconds)
        buffer[20] = flags.toByte()
        buffer[21] = fragmentIndex.toByte()
        buffer[22] = fragmentTotal.toByte()
        System.arraycopy(payload, 0, buffer, 24, payload.size)
        return buffer
    }

    private fun writeInt(buffer: ByteArray, offset: Int, value: Int) {
        buffer[offset] = ((value ushr 24) and 0xFF).toByte()
        buffer[offset + 1] = ((value ushr 16) and 0xFF).toByte()
        buffer[offset + 2] = ((value ushr 8) and 0xFF).toByte()
        buffer[offset + 3] = (value and 0xFF).toByte()
    }

    private fun writeLong(buffer: ByteArray, offset: Int, value: Long) {
        for (byteIndex in 0 until 8) {
            buffer[offset + byteIndex] = ((value ushr ((7 - byteIndex) * 8)) and 0xFFL).toByte()
        }
    }

    @Test
    fun `single fragment reassembles immediately`() {
        var now = 0L
        val reassembler = FragmentReassembler(timeoutMilliseconds = 500) { now }
        val packet: ByteArray = makePacket(
            streamId = 1, sequence = 10, presentationTimestampMicroseconds = 100,
            flags = PacketHeader.FLAG_IDR_FRAME or PacketHeader.FLAG_LAST_FRAGMENT,
            fragmentIndex = 0, fragmentTotal = 1,
            payload = byteArrayOf(1, 2, 3)
        )
        val frame: EncodedFrame? = reassembler.offer(PacketHeader.parse(packet, packet.size))
        assertEquals(1, frame?.streamId)
        assertEquals(10L, frame?.sequence)
        assertEquals(true, frame?.isIdrFrame)
        assertEquals(listOf<Byte>(1, 2, 3), frame?.payload?.toList())
    }

    @Test
    fun `three fragments reassemble in order`() {
        var now = 0L
        val reassembler = FragmentReassembler(timeoutMilliseconds = 500) { now }
        val sequences = intArrayOf(0, 1, 2)
        var completed: EncodedFrame? = null
        for (fragmentIndex in sequences) {
            val isLast: Boolean = fragmentIndex == 2
            val flags: Int = if (isLast) PacketHeader.FLAG_LAST_FRAGMENT else 0
            val packet: ByteArray = makePacket(
                streamId = 2, sequence = 99, presentationTimestampMicroseconds = 500,
                flags = flags, fragmentIndex = fragmentIndex, fragmentTotal = 3,
                payload = byteArrayOf((10 + fragmentIndex).toByte(), (20 + fragmentIndex).toByte())
            )
            val result: EncodedFrame? = reassembler.offer(PacketHeader.parse(packet, packet.size))
            if (result != null) completed = result
        }
        assertEquals(listOf<Byte>(10, 20, 11, 21, 12, 22), completed?.payload?.toList())
    }

    @Test
    fun `out of order fragments still reassemble`() {
        var now = 0L
        val reassembler = FragmentReassembler(timeoutMilliseconds = 500) { now }
        val indexes = intArrayOf(2, 0, 1)
        var completed: EncodedFrame? = null
        for (fragmentIndex in indexes) {
            val isLast: Boolean = fragmentIndex == 2
            val flags: Int = if (isLast) PacketHeader.FLAG_LAST_FRAGMENT else 0
            val packet: ByteArray = makePacket(
                streamId = 2, sequence = 50, presentationTimestampMicroseconds = 0,
                flags = flags, fragmentIndex = fragmentIndex, fragmentTotal = 3,
                payload = byteArrayOf((100 + fragmentIndex).toByte())
            )
            val result: EncodedFrame? = reassembler.offer(PacketHeader.parse(packet, packet.size))
            if (result != null) completed = result
        }
        assertEquals(listOf<Byte>(100, 101, 102), completed?.payload?.toList())
    }

    @Test
    fun `duplicate fragment is idempotent`() {
        var now = 0L
        val reassembler = FragmentReassembler(timeoutMilliseconds = 500) { now }
        val firstPacket: ByteArray = makePacket(
            streamId = 1, sequence = 5, presentationTimestampMicroseconds = 0,
            flags = 0, fragmentIndex = 0, fragmentTotal = 2,
            payload = byteArrayOf(0x01)
        )
        val firstDuplicate: ByteArray = firstPacket.copyOf()
        val lastPacket: ByteArray = makePacket(
            streamId = 1, sequence = 5, presentationTimestampMicroseconds = 0,
            flags = PacketHeader.FLAG_LAST_FRAGMENT, fragmentIndex = 1, fragmentTotal = 2,
            payload = byteArrayOf(0x02)
        )
        assertNull(reassembler.offer(PacketHeader.parse(firstPacket, firstPacket.size)))
        assertNull(reassembler.offer(PacketHeader.parse(firstDuplicate, firstDuplicate.size)))
        val frame: EncodedFrame? = reassembler.offer(PacketHeader.parse(lastPacket, lastPacket.size))
        assertEquals(listOf<Byte>(0x01, 0x02), frame?.payload?.toList())
    }

    @Test
    fun `incomplete reassembly is evicted after timeout`() {
        var now = 0L
        val reassembler = FragmentReassembler(timeoutMilliseconds = 500) { now }
        val partialPacket: ByteArray = makePacket(
            streamId = 1, sequence = 7, presentationTimestampMicroseconds = 0,
            flags = 0, fragmentIndex = 0, fragmentTotal = 3,
            payload = byteArrayOf(1)
        )
        reassembler.offer(PacketHeader.parse(partialPacket, partialPacket.size))
        now = 501
        val evicted: List<Long> = reassembler.evictTimedOut()
        assertEquals(listOf(7L), evicted)
    }

    @Test
    fun `interleaved streams are kept separate`() {
        var now = 0L
        val reassembler = FragmentReassembler(timeoutMilliseconds = 500) { now }
        val firstStreamPart1: ByteArray = makePacket(1, 1, 0, 0, 0, 2, byteArrayOf(0x0A))
        val secondStreamPart1: ByteArray = makePacket(2, 1, 0, 0, 0, 2, byteArrayOf(0x0B))
        val firstStreamPart2: ByteArray = makePacket(1, 1, 0, PacketHeader.FLAG_LAST_FRAGMENT, 1, 2, byteArrayOf(0x1A))
        val secondStreamPart2: ByteArray = makePacket(2, 1, 0, PacketHeader.FLAG_LAST_FRAGMENT, 1, 2, byteArrayOf(0x1B))
        assertNull(reassembler.offer(PacketHeader.parse(firstStreamPart1, firstStreamPart1.size)))
        assertNull(reassembler.offer(PacketHeader.parse(secondStreamPart1, secondStreamPart1.size)))
        val frameStreamOne: EncodedFrame? = reassembler.offer(PacketHeader.parse(firstStreamPart2, firstStreamPart2.size))
        val frameStreamTwo: EncodedFrame? = reassembler.offer(PacketHeader.parse(secondStreamPart2, secondStreamPart2.size))
        assertEquals(listOf<Byte>(0x0A, 0x1A), frameStreamOne?.payload?.toList())
        assertEquals(listOf<Byte>(0x0B, 0x1B), frameStreamTwo?.payload?.toList())
    }

    @Test
    fun `evictTimedOut returns empty list when nothing has timed out`() {
        var now = 0L
        val reassembler = FragmentReassembler(timeoutMilliseconds = 500) { now }
        val partialPacket: ByteArray = makePacket(
            streamId = 1, sequence = 9, presentationTimestampMicroseconds = 0,
            flags = 0, fragmentIndex = 0, fragmentTotal = 2,
            payload = byteArrayOf(1)
        )
        reassembler.offer(PacketHeader.parse(partialPacket, partialPacket.size))
        now = 499 // still within timeout window
        val evicted: List<Long> = reassembler.evictTimedOut()
        assertEquals(emptyList<Long>(), evicted)
    }
}
