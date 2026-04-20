package com.mtschoen.windowstream.viewer.decoder

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

class ParameterSetParserTest {
    private val spsNalUnitType: Byte = 0x67.toByte()
    private val ppsNalUnitType: Byte = 0x68.toByte()
    private val idrNalUnitType: Byte = 0x65.toByte()

    private val startCode: ByteArray = byteArrayOf(0, 0, 0, 1)

    @Test
    fun `extracts sps and pps from annex-b stream`() {
        val spsPayload: ByteArray = byteArrayOf(spsNalUnitType, 0x42, 0x00, 0x1F, 0x01, 0x02)
        val ppsPayload: ByteArray = byteArrayOf(ppsNalUnitType, 0x0A, 0x0B)
        val idrPayload: ByteArray = byteArrayOf(idrNalUnitType, 0x99.toByte())
        val combined: ByteArray =
            startCode + spsPayload + startCode + ppsPayload + startCode + idrPayload

        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertNotNull(parsed.sequenceParameterSet)
        assertNotNull(parsed.pictureParameterSet)
        assertEquals(spsPayload.toList(), parsed.sequenceParameterSet!!.toList())
        assertEquals(ppsPayload.toList(), parsed.pictureParameterSet!!.toList())
    }

    @Test
    fun `returns nulls when sps missing`() {
        val ppsPayload: ByteArray = byteArrayOf(ppsNalUnitType, 0x0A, 0x0B)
        val combined: ByteArray = startCode + ppsPayload
        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertNull(parsed.sequenceParameterSet)
        assertEquals(ppsPayload.toList(), parsed.pictureParameterSet?.toList())
    }

    @Test
    fun `handles three-byte start codes`() {
        val spsPayload: ByteArray = byteArrayOf(spsNalUnitType, 0x42)
        val threeByteStart: ByteArray = byteArrayOf(0, 0, 1)
        val combined: ByteArray = threeByteStart + spsPayload
        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertEquals(spsPayload.toList(), parsed.sequenceParameterSet?.toList())
    }

    @Test
    fun `ignores duplicate sps and pps keeping only first occurrence`() {
        val spsPayload: ByteArray = byteArrayOf(spsNalUnitType, 0x42)
        val spsPayloadDuplicate: ByteArray = byteArrayOf(spsNalUnitType, 0x43)
        val ppsPayload: ByteArray = byteArrayOf(ppsNalUnitType, 0x0A)
        val ppsPayloadDuplicate: ByteArray = byteArrayOf(ppsNalUnitType, 0x0B)
        val combined: ByteArray =
            startCode + spsPayload + startCode + ppsPayload +
            startCode + spsPayloadDuplicate + startCode + ppsPayloadDuplicate
        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertEquals(spsPayload.toList(), parsed.sequenceParameterSet?.toList())
        assertEquals(ppsPayload.toList(), parsed.pictureParameterSet?.toList())
    }

    @Test
    fun `returns empty when stream has no start codes`() {
        val combined: ByteArray = byteArrayOf(0x01, 0x02, 0x03)
        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertNull(parsed.sequenceParameterSet)
        assertNull(parsed.pictureParameterSet)
    }

    @Test
    fun `handles trailing start code with no nal data`() {
        val spsPayload: ByteArray = byteArrayOf(spsNalUnitType, 0x42)
        // trailing four-byte start code with no following NAL bytes
        val combined: ByteArray = startCode + spsPayload + startCode
        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertEquals(spsPayload.toList(), parsed.sequenceParameterSet?.toList())
    }

    @Test
    fun `handles three-byte start code at end of stream with no following bytes`() {
        // A stream that is exactly [0, 0, 1] — a 3-byte start code with nothing after it.
        // positionAfterStartCode receives position=0 and startPosition+3 (3) is not < bytes.size (3),
        // exercising the false branch of the size check.
        val combined: ByteArray = byteArrayOf(0, 0, 1)
        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertEquals(null, parsed.sequenceParameterSet)
        assertEquals(null, parsed.pictureParameterSet)
    }

    @Test
    fun `ignores four-zero sequence that is not a valid start code`() {
        // Bytes [0, 0, 0, 2, 0x67, 0x42] — looks like a 4-byte start code pattern except the
        // fourth byte is 2 instead of 1.  This exercises the isFourByte false branch when
        // index < bytes.size-3 is true but bytes[index+3] != 1.
        val combined: ByteArray = byteArrayOf(0, 0, 0, 2, spsNalUnitType, 0x42)
        val parsed: ParameterSets = ParameterSetParser.extract(combined)
        assertEquals(null, parsed.sequenceParameterSet)
        assertEquals(null, parsed.pictureParameterSet)
    }
}

class ParameterSetsTest {
    @Test
    fun `equals returns true for same instance`() {
        val parameterSets = ParameterSets(byteArrayOf(1), byteArrayOf(2))
        assertEquals(parameterSets, parameterSets)
    }

    @Test
    fun `equals returns false for different type`() {
        val parameterSets = ParameterSets(byteArrayOf(1), byteArrayOf(2))
        assertNotEquals(parameterSets, "not a ParameterSets")
    }

    @Test
    fun `equals returns true for equal byte arrays`() {
        val first = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        val second = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        assertEquals(first, second)
    }

    @Test
    fun `equals returns false for different sps bytes`() {
        val first = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        val second = ParameterSets(byteArrayOf(0x67, 0x43), byteArrayOf(0x68, 0x0A))
        assertNotEquals(first, second)
    }

    @Test
    fun `equals returns false for different pps bytes`() {
        val first = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        val second = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0B))
        assertNotEquals(first, second)
    }

    @Test
    fun `equals handles null sps in both`() {
        val first = ParameterSets(null, byteArrayOf(0x68, 0x0A))
        val second = ParameterSets(null, byteArrayOf(0x68, 0x0A))
        assertEquals(first, second)
    }

    @Test
    fun `equals returns false when one sps is null and other is not`() {
        val first = ParameterSets(null, byteArrayOf(0x68, 0x0A))
        val second = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        assertNotEquals(first, second)
    }

    @Test
    fun `equals handles null pps in both`() {
        val first = ParameterSets(byteArrayOf(0x67, 0x42), null)
        val second = ParameterSets(byteArrayOf(0x67, 0x42), null)
        assertEquals(first, second)
    }

    @Test
    fun `equals returns false when one pps is null and other is not`() {
        val first = ParameterSets(byteArrayOf(0x67, 0x42), null)
        val second = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        assertNotEquals(first, second)
    }

    @Test
    fun `hashCode produces same value for equal instances`() {
        val first = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        val second = ParameterSets(byteArrayOf(0x67, 0x42), byteArrayOf(0x68, 0x0A))
        assertEquals(first.hashCode(), second.hashCode())
    }

    @Test
    fun `hashCode handles null sps`() {
        val parameterSets = ParameterSets(null, byteArrayOf(0x68, 0x0A))
        assertEquals(ParameterSets(null, byteArrayOf(0x68, 0x0A)).hashCode(), parameterSets.hashCode())
    }

    @Test
    fun `hashCode handles null pps`() {
        val parameterSets = ParameterSets(byteArrayOf(0x67, 0x42), null)
        assertEquals(ParameterSets(byteArrayOf(0x67, 0x42), null).hashCode(), parameterSets.hashCode())
    }
}
