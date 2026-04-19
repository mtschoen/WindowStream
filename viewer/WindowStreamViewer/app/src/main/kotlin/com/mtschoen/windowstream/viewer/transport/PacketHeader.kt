package com.mtschoen.windowstream.viewer.transport

class MalformedPacketException(message: String) : RuntimeException(message)

class PacketHeader internal constructor(
    val streamId: Int,
    val sequence: Long,
    val presentationTimestampMicroseconds: Long,
    val flags: Int,
    val fragmentIndex: Int,
    val fragmentTotal: Int,
    private val buffer: ByteArray,
    private val payloadOffset: Int,
    val payloadLength: Int
) {
    val isIdrFrame: Boolean get() = (flags and FLAG_IDR_FRAME) != 0
    val isLastFragment: Boolean get() = (flags and FLAG_LAST_FRAGMENT) != 0

    fun payloadAt(index: Int): Byte {
        require(index in 0 until payloadLength) { "payload index $index out of range 0..<$payloadLength" }
        return buffer[payloadOffset + index]
    }

    fun copyPayloadInto(destination: ByteArray, destinationOffset: Int) {
        System.arraycopy(buffer, payloadOffset, destination, destinationOffset, payloadLength)
    }

    companion object {
        const val HEADER_BYTE_LENGTH: Int = 24
        const val MAXIMUM_PAYLOAD_BYTE_LENGTH: Int = 1200
        const val MAGIC_VALUE: Int = 0x57535452
        const val FLAG_IDR_FRAME: Int = 0x01
        const val FLAG_LAST_FRAGMENT: Int = 0x02

        fun parse(buffer: ByteArray, length: Int): PacketHeader {
            if (length < HEADER_BYTE_LENGTH) {
                throw MalformedPacketException("packet of $length bytes shorter than header ($HEADER_BYTE_LENGTH)")
            }
            val magic: Int = readBigEndianInt(buffer, 0)
            if (magic != MAGIC_VALUE) {
                throw MalformedPacketException("unexpected magic: 0x${magic.toString(16)}")
            }
            val streamId: Int = readBigEndianInt(buffer, 4)
            val sequence: Long = readBigEndianInt(buffer, 8).toLong() and 0xFFFF_FFFFL
            val presentationTimestampMicroseconds: Long = readBigEndianLong(buffer, 12)
            val flags: Int = buffer[20].toInt() and 0xFF
            val fragmentIndex: Int = buffer[21].toInt() and 0xFF
            val fragmentTotal: Int = buffer[22].toInt() and 0xFF
            if (fragmentTotal == 0) {
                throw MalformedPacketException("fragmentTotal must be >= 1")
            }
            if (fragmentIndex >= fragmentTotal) {
                throw MalformedPacketException("fragmentIndex $fragmentIndex >= fragmentTotal $fragmentTotal")
            }
            val payloadLength: Int = length - HEADER_BYTE_LENGTH
            if (payloadLength > MAXIMUM_PAYLOAD_BYTE_LENGTH) {
                throw MalformedPacketException("payload $payloadLength exceeds maximum $MAXIMUM_PAYLOAD_BYTE_LENGTH")
            }
            return PacketHeader(
                streamId = streamId,
                sequence = sequence,
                presentationTimestampMicroseconds = presentationTimestampMicroseconds,
                flags = flags,
                fragmentIndex = fragmentIndex,
                fragmentTotal = fragmentTotal,
                buffer = buffer,
                payloadOffset = HEADER_BYTE_LENGTH,
                payloadLength = payloadLength
            )
        }

        private fun readBigEndianInt(buffer: ByteArray, offset: Int): Int {
            return ((buffer[offset].toInt() and 0xFF) shl 24) or
                ((buffer[offset + 1].toInt() and 0xFF) shl 16) or
                ((buffer[offset + 2].toInt() and 0xFF) shl 8) or
                (buffer[offset + 3].toInt() and 0xFF)
        }

        private fun readBigEndianLong(buffer: ByteArray, offset: Int): Long {
            var result = 0L
            for (index in 0 until 8) {
                result = (result shl 8) or (buffer[offset + index].toLong() and 0xFFL)
            }
            return result
        }
    }
}
