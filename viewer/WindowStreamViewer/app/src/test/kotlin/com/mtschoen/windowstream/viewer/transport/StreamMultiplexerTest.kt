package com.mtschoen.windowstream.viewer.transport

import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import kotlin.time.Duration.Companion.seconds

class StreamMultiplexerTest {

    private fun frame(streamId: Int, sequence: Long = 0L): EncodedFrame =
        EncodedFrame(
            streamId = streamId,
            sequence = sequence,
            presentationTimestampMicroseconds = 0L,
            isIdrFrame = false,
            payload = byteArrayOf()
        )

    @Test
    fun `two stream ids receive only their own frames`() = runBlocking {
        val multiplexer = StreamMultiplexer()

        val flowOne = multiplexer.flowFor(streamId = 1)
        val flowTwo = multiplexer.flowFor(streamId = 2)

        multiplexer.route(frame(streamId = 1, sequence = 10))
        multiplexer.route(frame(streamId = 2, sequence = 20))
        multiplexer.route(frame(streamId = 1, sequence = 11))

        val firstFromOne: EncodedFrame = withTimeout(2.seconds) { flowOne.first() }
        val firstFromTwo: EncodedFrame = withTimeout(2.seconds) { flowTwo.first() }

        assertEquals(1, firstFromOne.streamId)
        assertEquals(10L, firstFromOne.sequence)
        assertEquals(2, firstFromTwo.streamId)
        assertEquals(20L, firstFromTwo.sequence)

        // A second take from flowOne should yield the second frame for stream 1
        val secondFromOne: EncodedFrame = withTimeout(2.seconds) { flowOne.first() }
        assertEquals(1, secondFromOne.streamId)
        assertEquals(11L, secondFromOne.sequence)
    }

    @Test
    fun `frames routed before flowFor is called are queued and delivered`() = runBlocking {
        val multiplexer = StreamMultiplexer()

        // Route frames before any collector is attached
        multiplexer.route(frame(streamId = 5, sequence = 100))
        multiplexer.route(frame(streamId = 5, sequence = 101))

        // Now attach a collector — frames should already be queued
        val collected: List<EncodedFrame> = withTimeout(2.seconds) {
            multiplexer.flowFor(streamId = 5).take(2).toList()
        }

        assertEquals(2, collected.size)
        assertEquals(100L, collected[0].sequence)
        assertEquals(101L, collected[1].sequence)
    }

    @Test
    fun `closeStream completes the flow for that stream`() = runBlocking {
        val multiplexer = StreamMultiplexer()

        multiplexer.route(frame(streamId = 3, sequence = 1))

        val collected: MutableList<EncodedFrame> = mutableListOf()
        val collectorJob = launch {
            multiplexer.flowFor(streamId = 3).collect { collected.add(it) }
        }

        // Give the collector a moment to receive the initial frame
        withTimeout(2.seconds) {
            while (collected.isEmpty()) {
                kotlinx.coroutines.delay(10)
            }
        }

        // Close the stream — should complete the flow and unblock the collector job
        multiplexer.closeStream(streamId = 3)
        withTimeout(2.seconds) { collectorJob.join() }

        assertEquals(1, collected.size)
        assertEquals(3, collected[0].streamId)
    }

    @Test
    fun `closeStream on a stream with no registered channel does nothing`() = runBlocking {
        val multiplexer = StreamMultiplexer()
        // Should not throw
        multiplexer.closeStream(streamId = 99)
    }

    @Test
    fun `slow consumer on one stream does not block routing for another stream`() = runBlocking {
        // Use a very small capacity so stream 7 fills quickly
        val multiplexer = StreamMultiplexer(perStreamCapacity = 2)

        val flowEight = multiplexer.flowFor(streamId = 8)

        // Route many frames to stream 7 (no one is consuming) and stream 8
        repeat(10) { index ->
            multiplexer.route(frame(streamId = 7, sequence = index.toLong()))
            multiplexer.route(frame(streamId = 8, sequence = index.toLong()))
        }

        // Stream 8 should still be receivable despite stream 7 being a slow consumer
        val frameFromEight: EncodedFrame = withTimeout(2.seconds) { flowEight.first() }
        assertEquals(8, frameFromEight.streamId)
    }

    @Test
    fun `flowFor and route create the same channel when called in different orders`() = runBlocking {
        val multiplexer = StreamMultiplexer()

        // flowFor first, then route
        val flow = multiplexer.flowFor(streamId = 42)
        multiplexer.route(frame(streamId = 42, sequence = 77))

        val received: EncodedFrame = withTimeout(2.seconds) { flow.first() }
        assertEquals(42, received.streamId)
        assertEquals(77L, received.sequence)
    }
}
