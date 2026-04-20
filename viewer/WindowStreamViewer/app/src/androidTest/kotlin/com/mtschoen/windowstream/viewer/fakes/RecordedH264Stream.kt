package com.mtschoen.windowstream.viewer.fakes

import java.io.InputStream

object RecordedH264Stream {
    fun loadAnnexBFrames(input: InputStream): List<ByteArray> {
        val raw: ByteArray = input.readBytes()
        val frames: MutableList<ByteArray> = mutableListOf()
        var cursor = 0
        while (cursor < raw.size) {
            val startIndex: Int = findStartCode(raw, cursor) ?: break
            val nextStart: Int = findStartCode(raw, startIndex + 4) ?: raw.size
            frames.add(raw.copyOfRange(startIndex, nextStart))
            cursor = nextStart
        }
        return frames
    }

    private fun findStartCode(bytes: ByteArray, offset: Int): Int? {
        var index: Int = offset
        while (index < bytes.size - 3) {
            if (bytes[index] == 0.toByte() && bytes[index + 1] == 0.toByte() &&
                bytes[index + 2] == 0.toByte() && bytes[index + 3] == 1.toByte()) {
                return index
            }
            index++
        }
        return null
    }
}
