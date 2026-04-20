package com.mtschoen.windowstream.viewer.decoder

data class ParameterSets(
    val sequenceParameterSet: ByteArray?,
    val pictureParameterSet: ByteArray?
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ParameterSets) return false
        return sequenceParameterSet?.contentEquals(other.sequenceParameterSet) ?: (other.sequenceParameterSet == null) &&
            pictureParameterSet?.contentEquals(other.pictureParameterSet) ?: (other.pictureParameterSet == null)
    }

    override fun hashCode(): Int {
        var result = sequenceParameterSet?.contentHashCode() ?: 0
        result = 31 * result + (pictureParameterSet?.contentHashCode() ?: 0)
        return result
    }
}

object ParameterSetParser {
    private const val SEQUENCE_PARAMETER_SET_NAL_TYPE: Int = 7
    private const val PICTURE_PARAMETER_SET_NAL_TYPE: Int = 8
    private const val NAL_UNIT_TYPE_MASK: Int = 0x1F

    fun extract(annexBStream: ByteArray): ParameterSets {
        var sequenceParameterSet: ByteArray? = null
        var pictureParameterSet: ByteArray? = null

        val startPositions: List<Int> = findStartCodePositions(annexBStream)
        for (startIndex in startPositions.indices) {
            val startPosition: Int = startPositions[startIndex]
            val endPosition: Int = if (startIndex + 1 < startPositions.size) {
                startPositions[startIndex + 1]
            } else {
                annexBStream.size
            }
            val nalStart: Int = positionAfterStartCode(annexBStream, startPosition)
            if (nalStart >= endPosition) continue
            val nalUnitByte: Int = annexBStream[nalStart].toInt() and 0xFF
            val nalUnitType: Int = nalUnitByte and NAL_UNIT_TYPE_MASK
            val nalBytes: ByteArray = annexBStream.copyOfRange(nalStart, endPosition)
            when (nalUnitType) {
                SEQUENCE_PARAMETER_SET_NAL_TYPE -> if (sequenceParameterSet == null) sequenceParameterSet = nalBytes
                PICTURE_PARAMETER_SET_NAL_TYPE -> if (pictureParameterSet == null) pictureParameterSet = nalBytes
            }
        }
        return ParameterSets(sequenceParameterSet, pictureParameterSet)
    }

    private fun findStartCodePositions(bytes: ByteArray): List<Int> {
        val positions: MutableList<Int> = mutableListOf()
        var index = 0
        while (index < bytes.size - 2) {
            val isThreeByte: Boolean = bytes[index] == 0.toByte() && bytes[index + 1] == 0.toByte() && bytes[index + 2] == 1.toByte()
            val isFourByte: Boolean = index < bytes.size - 3 &&
                bytes[index] == 0.toByte() && bytes[index + 1] == 0.toByte() &&
                bytes[index + 2] == 0.toByte() && bytes[index + 3] == 1.toByte()
            if (isFourByte) {
                positions.add(index)
                index += 4
            } else if (isThreeByte) {
                positions.add(index)
                index += 3
            } else {
                index++
            }
        }
        return positions
    }

    private fun positionAfterStartCode(bytes: ByteArray, startPosition: Int): Int {
        val isFourByteStartCode: Boolean = startPosition + 3 < bytes.size && bytes[startPosition + 3] == 1.toByte()
        return if (isFourByteStartCode) startPosition + 4 else startPosition + 3
    }
}
