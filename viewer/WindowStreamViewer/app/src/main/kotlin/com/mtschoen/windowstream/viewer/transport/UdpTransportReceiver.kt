package com.mtschoen.windowstream.viewer.transport

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress

class UdpTransportReceiver(
    private val bindAddress: InetAddress,
    private val requestedPort: Int,
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
        val emissionFlow = MutableSharedFlow<EncodedFrame>(
            replay = 0,
            extraBufferCapacity = 64,
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
                    if (frame != null) emissionFlow.emit(frame)
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
        return emissionFlow.asSharedFlow()
    }

    fun close() {
        receiveJob?.cancel()
        evictionJob?.cancel()
        runCatching { socket?.close() }
    }
}
