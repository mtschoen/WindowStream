package com.mtschoen.windowstream.viewer.transport

import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Demultiplexes a single [Flow]<[EncodedFrame]> into per-stream sub-flows keyed by
 * [EncodedFrame.streamId]. Sub-flows are created lazily on first demand via [flowFor]
 * or on first frame arrival via [route].
 *
 * Backpressure: each per-stream channel is bounded with [BufferOverflow.DROP_OLDEST]
 * semantics. A slow consumer for one streamId does NOT block the multiplexer's input —
 * frames for that stream are dropped so other streams stay live.
 */
class StreamMultiplexer(
    private val perStreamCapacity: Int = 64
) {
    private val streamChannels: MutableMap<Int, Channel<EncodedFrame>> = mutableMapOf()
    private val lock: Mutex = Mutex()

    /**
     * Routes [frame] to the per-stream channel for [EncodedFrame.streamId].
     * Creates the channel lazily if it does not yet exist. Never suspends the caller
     * due to backpressure — excess frames are dropped (oldest first).
     */
    suspend fun route(frame: EncodedFrame) {
        val channel: Channel<EncodedFrame> = lock.withLock {
            streamChannels.getOrPut(frame.streamId) { makeChannel() }
        }
        channel.trySend(frame)
    }

    /**
     * Returns a [Flow] of [EncodedFrame] for the given [streamId]. Creates the
     * underlying channel lazily if it has not been created yet, so frames routed
     * before [flowFor] is called will queue and be delivered in order.
     *
     * The flow completes when [closeStream] is called for this [streamId].
     */
    suspend fun flowFor(streamId: Int): Flow<EncodedFrame> {
        val channel: Channel<EncodedFrame> = lock.withLock {
            streamChannels.getOrPut(streamId) { makeChannel() }
        }
        return channel.receiveAsFlow()
    }

    /**
     * Closes the per-stream channel for [streamId], completing any collector of
     * [flowFor] for that stream. Does nothing if the stream is not registered.
     */
    suspend fun closeStream(streamId: Int) {
        lock.withLock {
            streamChannels.remove(streamId)?.close()
        }
    }

    private fun makeChannel(): Channel<EncodedFrame> =
        Channel(capacity = perStreamCapacity, onBufferOverflow = BufferOverflow.DROP_OLDEST)
}
