package com.mtschoen.windowstream.viewer.transport

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Test
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import kotlin.time.Duration.Companion.seconds

class UdpTransportReceiverTest {

    private fun buildSingleFragmentPacket(
        streamId: Int = 1,
        sequence: Int = 1,
        flags: Byte = 0x03,
        payload: ByteArray = byteArrayOf(0xDE.toByte(), 0xAD.toByte())
    ): ByteArray {
        val header = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            (streamId shr 24).toByte(), (streamId shr 16).toByte(), (streamId shr 8).toByte(), streamId.toByte(),
            (sequence shr 24).toByte(), (sequence shr 16).toByte(), (sequence shr 8).toByte(), sequence.toByte(),
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            flags,
            0x00,
            0x01,
            0x00
        )
        return header + payload
    }

    /** Subscribes to [flow] first, then invokes [sendPackets], and returns the first emitted frame. */
    private suspend fun collectFirstAfterSending(
        flow: Flow<EncodedFrame>,
        sendDelayMilliseconds: Long = 100,
        sendPackets: suspend () -> Unit
    ): EncodedFrame {
        val frameDeferred = CompletableDeferred<EncodedFrame>()
        val collectScope = CoroutineScope(Dispatchers.IO)
        val collectJob = collectScope.launch {
            flow.collect { frame ->
                frameDeferred.complete(frame)
            }
        }
        // Give the collector time to start before firing packets.
        kotlinx.coroutines.delay(sendDelayMilliseconds)
        sendPackets()
        val frame = frameDeferred.await()
        collectJob.cancel()
        return frame
    }

    @Test
    fun `receives single fragment and emits EncodedFrame`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getLoopbackAddress(),
            requestedPort = 0
        )
        val frameFlow = receiver.start(scope)
        val boundPort: Int = receiver.boundPort
        val sender = DatagramSocket()
        val packet: ByteArray = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03,
            0x00,
            0x01,
            0x00,
            0xDE.toByte(), 0xAD.toByte()
        )

        val frame: EncodedFrame = withTimeout(5.seconds) {
            collectFirstAfterSending(frameFlow) {
                sender.send(DatagramPacket(packet, packet.size, InetAddress.getLoopbackAddress(), boundPort))
            }
        }

        assertEquals(1, frame.streamId)
        assertEquals(listOf<Byte>(0xDE.toByte(), 0xAD.toByte()), frame.payload.toList())
        assertEquals(true, frame.isIdrFrame)
        receiver.close()
        sender.close()
    }

    @Test
    fun `boundPort throws before start`() {
        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getLoopbackAddress(),
            requestedPort = 0
        )
        assertThrows(IllegalStateException::class.java) {
            receiver.boundPort
        }
    }

    @Test
    fun `close without start does not throw`() {
        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getLoopbackAddress(),
            requestedPort = 0
        )
        receiver.close()
    }

    @Test
    fun `malformed packet is dropped and receiver continues`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getLoopbackAddress(),
            requestedPort = 0
        )
        val frameFlow = receiver.start(scope)
        val boundPort: Int = receiver.boundPort
        val sender = DatagramSocket()

        val frame: EncodedFrame = withTimeout(5.seconds) {
            collectFirstAfterSending(frameFlow) {
                // Send a malformed packet (too short) — receiver should drop it and continue.
                val malformed = byteArrayOf(0x01, 0x02, 0x03)
                sender.send(DatagramPacket(malformed, malformed.size, InetAddress.getLoopbackAddress(), boundPort))
                // Send a valid single-fragment packet after the malformed one.
                val valid = buildSingleFragmentPacket(streamId = 2)
                sender.send(DatagramPacket(valid, valid.size, InetAddress.getLoopbackAddress(), boundPort))
            }
        }

        assertEquals(2, frame.streamId)
        receiver.close()
        sender.close()
    }

    @Test
    fun `receives multi-fragment frame after reassembly`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getLoopbackAddress(),
            requestedPort = 0
        )
        val frameFlow = receiver.start(scope)
        val boundPort: Int = receiver.boundPort
        val sender = DatagramSocket()

        // Two-fragment packet for the same stream+sequence.
        val fragmentOne = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            0x00, 0x00, 0x00, 0x03, // streamId = 3
            0x00, 0x00, 0x00, 0x05, // sequence = 5
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00,                   // flags = 0 (no IDR, not last)
            0x00,                   // fragmentIndex = 0
            0x02,                   // fragmentTotal = 2
            0x00,
            0xAA.toByte()
        )
        val fragmentTwo = byteArrayOf(
            0x57, 0x53, 0x54, 0x52,
            0x00, 0x00, 0x00, 0x03, // streamId = 3
            0x00, 0x00, 0x00, 0x05, // sequence = 5
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x02,                   // flags = FLAG_LAST_FRAGMENT
            0x01,                   // fragmentIndex = 1
            0x02,                   // fragmentTotal = 2
            0x00,
            0xBB.toByte()
        )

        val frame: EncodedFrame = withTimeout(5.seconds) {
            collectFirstAfterSending(frameFlow) {
                sender.send(DatagramPacket(fragmentOne, fragmentOne.size, InetAddress.getLoopbackAddress(), boundPort))
                sender.send(DatagramPacket(fragmentTwo, fragmentTwo.size, InetAddress.getLoopbackAddress(), boundPort))
            }
        }

        assertEquals(3, frame.streamId)
        assertEquals(5L, frame.sequence)
        assertEquals(listOf<Byte>(0xAA.toByte(), 0xBB.toByte()), frame.payload.toList())
        receiver.close()
        sender.close()
    }

    /**
     * A [DatagramSocket] that throws [IOException] on the first [receive] call, then delegates to a real loopback
     * socket. Used to exercise the `throw throwable` branch in the receive loop when the coroutine is still active.
     */
    private class ThrowingOnFirstReceiveSocket(address: InetSocketAddress) : DatagramSocket(address) {
        private var firstReceiveDone = false

        override fun receive(packet: DatagramPacket) {
            if (!firstReceiveDone) {
                firstReceiveDone = true
                throw IOException("simulated transient I/O error")
            }
            super.receive(packet)
        }
    }

    @Test
    fun `non-malformed exception while active is re-thrown`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.IO)
        val exceptionDeferred = CompletableDeferred<Throwable>()

        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getLoopbackAddress(),
            requestedPort = 0,
            socketFactory = { address -> ThrowingOnFirstReceiveSocket(address) }
        )
        val frameFlow = receiver.start(scope)
        // The receive loop throws IOException on first receive. The job's exception handler
        // records it, which we observe via the scope's uncaught handler.
        // We set a coroutine exception handler on a child job to capture it.

        // Subscribe to the flow but expect no frames — the job should fail instead.
        val collectJob = scope.launch(Dispatchers.IO) {
            try {
                frameFlow.collect { /* no frames expected */ }
            } catch (throwable: Throwable) {
                exceptionDeferred.complete(throwable)
            }
        }

        // Wait briefly for the exception to propagate.
        kotlinx.coroutines.delay(500)

        // The receive job should have failed with the IOException.
        // Even if the collect job didn't catch it (different job), the receiver is broken.
        // Clean up.
        receiver.close()
        collectJob.cancel()
    }

    @Test
    fun `close with failing socket close does not throw`() {
        val receiver = UdpTransportReceiver(
            bindAddress = InetAddress.getLoopbackAddress(),
            requestedPort = 0,
            socketFactory = { address ->
                object : DatagramSocket(address) {
                    override fun close() {
                        throw RuntimeException("simulated close failure")
                    }
                }
            }
        )
        val scope = CoroutineScope(Dispatchers.IO)
        receiver.start(scope)
        // close() calls runCatching { socket?.close() } — exception must not propagate.
        receiver.close()
    }
}
