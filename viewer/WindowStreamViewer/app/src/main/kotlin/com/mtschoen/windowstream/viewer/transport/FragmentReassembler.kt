package com.mtschoen.windowstream.viewer.transport

class FragmentReassembler(
    private val timeoutMilliseconds: Long = 500,
    private val currentTimeMilliseconds: () -> Long = { System.currentTimeMillis() }
) {
    private data class PartialFrame(
        val streamId: Int,
        val sequence: Long,
        val presentationTimestampMicroseconds: Long,
        val fragmentTotal: Int,
        val fragments: Array<ByteArray?>,
        var fragmentsReceived: Int,
        var isIdrFrame: Boolean,
        val createdAtMilliseconds: Long
    )

    private val partialsByKey: MutableMap<Long, PartialFrame> = LinkedHashMap()

    private fun keyFor(streamId: Int, sequence: Long): Long =
        (streamId.toLong() shl 32) or (sequence and 0xFFFF_FFFFL)

    fun offer(header: PacketHeader): EncodedFrame? {
        val key: Long = keyFor(header.streamId, header.sequence)
        val existing: PartialFrame? = partialsByKey[key]
        val partial: PartialFrame = existing ?: PartialFrame(
            streamId = header.streamId,
            sequence = header.sequence,
            presentationTimestampMicroseconds = header.presentationTimestampMicroseconds,
            fragmentTotal = header.fragmentTotal,
            fragments = arrayOfNulls(header.fragmentTotal),
            fragmentsReceived = 0,
            isIdrFrame = header.isIdrFrame,
            createdAtMilliseconds = currentTimeMilliseconds()
        ).also { partialsByKey[key] = it }

        if (partial.fragments[header.fragmentIndex] != null) {
            return null
        }
        val payloadCopy = ByteArray(header.payloadLength)
        header.copyPayloadInto(payloadCopy, 0)
        partial.fragments[header.fragmentIndex] = payloadCopy
        partial.fragmentsReceived++
        if (header.isIdrFrame) partial.isIdrFrame = true

        if (partial.fragmentsReceived < partial.fragmentTotal) return null

        partialsByKey.remove(key)
        val totalLength: Int = partial.fragments.sumOf { it!!.size }
        val combined = ByteArray(totalLength)
        var writeOffset = 0
        for (fragmentBytes in partial.fragments) {
            val bytes: ByteArray = fragmentBytes!!
            System.arraycopy(bytes, 0, combined, writeOffset, bytes.size)
            writeOffset += bytes.size
        }
        return EncodedFrame(
            streamId = partial.streamId,
            sequence = partial.sequence,
            presentationTimestampMicroseconds = partial.presentationTimestampMicroseconds,
            isIdrFrame = partial.isIdrFrame,
            payload = combined
        )
    }

    fun evictTimedOut(): List<Long> {
        val now: Long = currentTimeMilliseconds()
        val evictedSequences: MutableList<Long> = mutableListOf()
        val iterator = partialsByKey.entries.iterator()
        while (iterator.hasNext()) {
            val entry = iterator.next()
            if (now - entry.value.createdAtMilliseconds >= timeoutMilliseconds) {
                evictedSequences.add(entry.value.sequence)
                iterator.remove()
            }
        }
        return evictedSequences
    }
}
