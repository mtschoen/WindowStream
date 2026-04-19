package com.mtschoen.windowstream.viewer.transport

data class EncodedFrame(
    val streamId: Int,
    val sequence: Long,
    val presentationTimestampMicroseconds: Long,
    val isIdrFrame: Boolean,
    val payload: ByteArray
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is EncodedFrame) return false
        return streamId == other.streamId &&
            sequence == other.sequence &&
            presentationTimestampMicroseconds == other.presentationTimestampMicroseconds &&
            isIdrFrame == other.isIdrFrame &&
            payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int {
        var result = streamId
        result = 31 * result + sequence.hashCode()
        result = 31 * result + presentationTimestampMicroseconds.hashCode()
        result = 31 * result + isIdrFrame.hashCode()
        result = 31 * result + payload.contentHashCode()
        return result
    }
}
