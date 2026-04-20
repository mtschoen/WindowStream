package com.mtschoen.windowstream.viewer.decoder

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
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
        assertEquals(null, parsed.sequenceParameterSet)
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
}
