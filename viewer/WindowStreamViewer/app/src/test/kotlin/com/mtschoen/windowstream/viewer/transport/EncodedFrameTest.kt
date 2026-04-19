package com.mtschoen.windowstream.viewer.transport

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class EncodedFrameTest {
    private fun makeFrame(
        streamId: Int = 1,
        sequence: Long = 1L,
        presentationTimestampMicroseconds: Long = 0L,
        isIdrFrame: Boolean = false,
        payload: ByteArray = byteArrayOf(1, 2, 3)
    ) = EncodedFrame(streamId, sequence, presentationTimestampMicroseconds, isIdrFrame, payload)

    @Test
    fun `equals returns true for identical frames`() {
        val frameA = makeFrame()
        val frameB = makeFrame()
        assertEquals(frameA, frameB)
    }

    @Test
    fun `equals returns true for same instance`() {
        val frame = makeFrame()
        assertEquals(frame, frame)
    }

    @Test
    fun `equals returns false for different type`() {
        val frame = makeFrame()
        assertFalse(frame.equals("not a frame"))
    }

    @Test
    fun `equals returns false when streamId differs`() {
        assertNotEquals(makeFrame(streamId = 1), makeFrame(streamId = 2))
    }

    @Test
    fun `equals returns false when sequence differs`() {
        assertNotEquals(makeFrame(sequence = 1L), makeFrame(sequence = 2L))
    }

    @Test
    fun `equals returns false when presentationTimestampMicroseconds differs`() {
        assertNotEquals(
            makeFrame(presentationTimestampMicroseconds = 100L),
            makeFrame(presentationTimestampMicroseconds = 200L)
        )
    }

    @Test
    fun `equals returns false when isIdrFrame differs`() {
        assertNotEquals(makeFrame(isIdrFrame = false), makeFrame(isIdrFrame = true))
    }

    @Test
    fun `equals returns false when payload differs`() {
        assertNotEquals(
            makeFrame(payload = byteArrayOf(1, 2, 3)),
            makeFrame(payload = byteArrayOf(4, 5, 6))
        )
    }

    @Test
    fun `hashCode is equal for identical frames`() {
        assertEquals(makeFrame().hashCode(), makeFrame().hashCode())
    }

    @Test
    fun `hashCode differs for frames with different payload`() {
        val hashA = makeFrame(payload = byteArrayOf(1)).hashCode()
        val hashB = makeFrame(payload = byteArrayOf(99)).hashCode()
        assertNotEquals(hashA, hashB)
    }
}
