package com.mtschoen.windowstream.viewer.fakes

import java.io.InputStream

object RecordedH264Stream {
    /**
     * Splits an Annex-B H.264 bitstream into individual NAL units.
     *
     * Both 3-byte (`00 00 01`) and 4-byte (`00 00 00 01`) start codes are
     * recognised. Each returned ByteArray includes the leading start code so
     * downstream consumers can use it as a self-contained NAL unit payload.
     */
    fun loadAnnexBFrames(input: InputStream): List<ByteArray> {
        val raw: ByteArray = input.readBytes()
        val startCodePositions: MutableList<Int> = mutableListOf()
        var index = 0
        while (index < raw.size - 2) {
            val isFourByte: Boolean = index < raw.size - 3 &&
                raw[index] == 0.toByte() && raw[index + 1] == 0.toByte() &&
                raw[index + 2] == 0.toByte() && raw[index + 3] == 1.toByte()
            val isThreeByte: Boolean = !isFourByte &&
                raw[index] == 0.toByte() && raw[index + 1] == 0.toByte() &&
                raw[index + 2] == 1.toByte()
            when {
                isFourByte -> { startCodePositions.add(index); index += 4 }
                isThreeByte -> { startCodePositions.add(index); index += 3 }
                else -> index++
            }
        }
        val nalUnits: MutableList<ByteArray> = mutableListOf()
        for (positionIndex in startCodePositions.indices) {
            val startPosition: Int = startCodePositions[positionIndex]
            val endPosition: Int = if (positionIndex + 1 < startCodePositions.size) {
                startCodePositions[positionIndex + 1]
            } else {
                raw.size
            }
            nalUnits.add(raw.copyOfRange(startPosition, endPosition))
        }
        return nalUnits
    }
}
