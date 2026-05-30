package com.mtschoen.windowstream.viewer.transport

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress

class UdpTransportReceiver(
    private val bindAddress: InetAddress,
    private val requestedPort: Int,
    private val emissionCapacity: Int = 1,
    private val socketFactory: (InetSocketAddress) -> DatagramSocket = { address ->
        DatagramSocket(address).also { it.receiveBufferSize = 2 * 1024 * 1024 }
    }
) {
    private var socket: DatagramSocket? = null
    private var receiveJob: Job? = null
    private var evictionJob: Job? = null
    private val reassembler = FragmentReassembler()

    val boundPort: Int get() = socket?.localPort ?: error("socket not bound")

    fun start(scope: CoroutineScope): Flow<EncodedFrame> {
        val datagramSocket = socketFactory(InetSocketAddress(bindAddress, requestedPort))
        socket = datagramSocket
        // Tier 1c: the default capacity=1 with DROP_OLDEST ensures the decoder
        // always picks up the freshest reassembled frame after any transient
        // stall, rather than processing a queue of up to 64 stale frames — this
        // tightens the enc→reasm tail-latency jitter for the single-stream case.
        // A shared multi-stream receiver (one socket feeding a StreamMultiplexer)
        // passes a larger emissionCapacity so frames for one stream are not
        // dropped by the arrival of a frame for a different stream.
        val emissionChannel = Channel<EncodedFrame>(
            capacity = emissionCapacity,
            onBufferOverflow = BufferOverflow.DROP_OLDEST
        )

        receiveJob = scope.launch(Dispatchers.IO) {
            val receiveBuffer = ByteArray(PacketHeader.HEADER_BYTE_LENGTH + PacketHeader.MAXIMUM_PAYLOAD_BYTE_LENGTH)
            val datagramPacket = DatagramPacket(receiveBuffer, receiveBuffer.size)
            while (isActive) {
                try {
                    datagramSocket.receive(datagramPacket)
                    val parsed: PacketHeader = PacketHeader.parse(receiveBuffer, datagramPacket.length)
                    val frame: EncodedFrame? = reassembler.offer(parsed)
                    if (frame != null) emissionChannel.trySend(frame)
                } catch (exception: MalformedPacketException) {
                    // Drop malformed packet and continue.
                } catch (throwable: Throwable) {
                    if (!isActive) break
                    throw throwable
                }
            }
        }

        evictionJob = scope.launch(Dispatchers.IO) {
            while (isActive) {
                reassembler.evictTimedOut()
                kotlinx.coroutines.delay(100)
            }
        }
        return emissionChannel.receiveAsFlow()
    }

    fun close() {
        receiveJob?.cancel()
        evictionJob?.cancel()
        runCatching { socket?.close() }
    }
}
