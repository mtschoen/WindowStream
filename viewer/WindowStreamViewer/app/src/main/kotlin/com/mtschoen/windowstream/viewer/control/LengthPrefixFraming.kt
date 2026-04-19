package com.mtschoen.windowstream.viewer.control

import java.io.InputStream
import java.io.OutputStream

class MalformedFrameException(message: String) : RuntimeException(message)

object LengthPrefixFraming {
    const val MAXIMUM_FRAME_LENGTH_BYTES: Int = 1_048_576

    fun writeFrame(output: OutputStream, payload: ByteArray) {
        val length: Int = payload.size
        output.write((length ushr 24) and 0xFF)
        output.write((length ushr 16) and 0xFF)
        output.write((length ushr 8) and 0xFF)
        output.write(length and 0xFF)
        output.write(payload)
        output.flush()
    }

    fun readFrame(input: InputStream): ByteArray? {
        val firstByte: Int = input.read()
        if (firstByte == -1) return null
        val secondByte: Int = input.read()
        val thirdByte: Int = input.read()
        val fourthByte: Int = input.read()
        if (secondByte == -1 || thirdByte == -1 || fourthByte == -1) {
            throw MalformedFrameException("truncated length prefix")
        }
        val length: Int = (firstByte shl 24) or (secondByte shl 16) or (thirdByte shl 8) or fourthByte
        if (length < 0) throw MalformedFrameException("negative length: $length")
        if (length > MAXIMUM_FRAME_LENGTH_BYTES) {
            throw MalformedFrameException("length exceeds maximum: $length")
        }
        val payload = ByteArray(length)
        var totalRead = 0
        while (totalRead < length) {
            val readCount: Int = input.read(payload, totalRead, length - totalRead)
            if (readCount == -1) throw MalformedFrameException("truncated payload at $totalRead of $length")
            totalRead += readCount
        }
        return payload
    }
}
