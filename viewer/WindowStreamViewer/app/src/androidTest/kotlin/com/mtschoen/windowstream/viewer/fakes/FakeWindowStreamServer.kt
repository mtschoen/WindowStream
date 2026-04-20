package com.mtschoen.windowstream.viewer.fakes

import com.mtschoen.windowstream.viewer.control.ActiveStreamDescriptor
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.LengthPrefixFraming
import com.mtschoen.windowstream.viewer.control.ProtocolSerialization
import com.mtschoen.windowstream.viewer.transport.PacketHeader
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket

/**
 * Minimal fake server that speaks the WindowStream control protocol over TCP and
 * sends a pre-recorded H.264 Annex-B stream over UDP to a known viewer UDP port.
 *
 * @param frames        Annex-B NAL unit byte arrays produced by [RecordedH264Stream].
 * @param viewerUdpPort The UDP port the viewer is listening on. Because the emulator loopback
 *                      test injects a fixed-port [UdpTransportReceiver], this value is
 *                      agreed on in advance by the test.
 * @param streamId      Stream identifier reported in [ActiveStreamDescriptor].
 * @param width         Frame width in pixels.
 * @param height        Frame height in pixels.
 * @param framesPerSecond Nominal frame rate used for presentation timestamps.
 */
class FakeWindowStreamServer(
    private val frames: List<ByteArray>,
    private val viewerUdpPort: Int,
    private val streamId: Int = 1,
    private val width: Int = 320,
    private val height: Int = 240,
    private val framesPerSecond: Int = 60
) {
    private lateinit var controlListener: ServerSocket
    private lateinit var udpSocket: DatagramSocket
    private var controlJob: Job? = null
    private var videoJob: Job? = null

    val controlPort: Int get() = controlListener.localPort

    fun start(scope: CoroutineScope) {
        controlListener = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        udpSocket = DatagramSocket()

        controlJob = scope.launch(Dispatchers.IO) {
            val client: Socket = controlListener.accept()
            client.use { socket ->
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                // Consume the viewer's HELLO frame.
                LengthPrefixFraming.readFrame(input)
                val serverHello: String = ProtocolSerialization.json.encodeToString(
                    ControlMessage.serializer(),
                    ControlMessage.ServerHello(
                        serverVersion = 1,
                        activeStream = ActiveStreamDescriptor(
                            streamId = streamId,
                            udpPort = viewerUdpPort,
                            codec = "h264",
                            width = width,
                            height = height,
                            framesPerSecond = framesPerSecond
                        )
                    )
                )
                LengthPrefixFraming.writeFrame(output, serverHello.toByteArray(Charsets.UTF_8))
                // Drain any further control messages (e.g. keyframe requests) without responding.
                while (true) {
                    LengthPrefixFraming.readFrame(input) ?: break
                }
            }
        }

        videoJob = scope.launch(Dispatchers.IO) {
            // Wait for the viewer to bind its UDP socket and for the control handshake to complete.
            delay(800)
            var sequence: Long = 0
            val targetAddress: InetAddress = InetAddress.getLoopbackAddress()
            for (nalBytes in frames) {
                emitFragmented(nalBytes, sequence, targetAddress)
                sequence++
                delay(1_000L / framesPerSecond)
            }
        }
    }

    private fun emitFragmented(nalBytes: ByteArray, sequence: Long, target: InetAddress) {
        val maximumPayload: Int = PacketHeader.MAXIMUM_PAYLOAD_BYTE_LENGTH
        val fragmentCount: Int = ((nalBytes.size + maximumPayload - 1) / maximumPayload).coerceAtLeast(1)
        for (fragmentIndex in 0 until fragmentCount) {
            val start: Int = fragmentIndex * maximumPayload
            val end: Int = minOf(nalBytes.size, start + maximumPayload)
            val slice: ByteArray = nalBytes.copyOfRange(start, end)
            val isLast: Boolean = fragmentIndex == fragmentCount - 1
            // The NAL unit type is encoded in the low 5 bits of the byte after the 4-byte start code.
            val nalUnitType: Int = if (nalBytes.size > 4) nalBytes[4].toInt() and 0x1F else 0
            val isIdrFrame: Boolean = nalUnitType == 5
            val flags: Int = (if (isIdrFrame) PacketHeader.FLAG_IDR_FRAME else 0) or
                (if (isLast) PacketHeader.FLAG_LAST_FRAGMENT else 0)
            val datagram: ByteArray = buildPacket(sequence, flags, fragmentIndex, fragmentCount, slice)
            udpSocket.send(DatagramPacket(datagram, datagram.size, target, viewerUdpPort))
        }
    }

    private fun buildPacket(
        sequence: Long,
        flags: Int,
        fragmentIndex: Int,
        fragmentTotal: Int,
        payload: ByteArray
    ): ByteArray {
        val buffer = ByteArray(PacketHeader.HEADER_BYTE_LENGTH + payload.size)
        // Magic: 'W' 'S' 'T' 'R'
        buffer[0] = 0x57; buffer[1] = 0x53; buffer[2] = 0x54; buffer[3] = 0x52
        writeInt(buffer, 4, streamId)
        writeInt(buffer, 8, sequence.toInt())
        writeLong(buffer, 12, sequence * (1_000_000L / framesPerSecond))
        buffer[20] = flags.toByte()
        buffer[21] = fragmentIndex.toByte()
        buffer[22] = fragmentTotal.toByte()
        // byte 23 is reserved — left as zero
        System.arraycopy(payload, 0, buffer, PacketHeader.HEADER_BYTE_LENGTH, payload.size)
        return buffer
    }

    private fun writeInt(buffer: ByteArray, offset: Int, value: Int) {
        buffer[offset] = ((value ushr 24) and 0xFF).toByte()
        buffer[offset + 1] = ((value ushr 16) and 0xFF).toByte()
        buffer[offset + 2] = ((value ushr 8) and 0xFF).toByte()
        buffer[offset + 3] = (value and 0xFF).toByte()
    }

    private fun writeLong(buffer: ByteArray, offset: Int, value: Long) {
        for (index in 0 until 8) {
            buffer[offset + index] = ((value ushr ((7 - index) * 8)) and 0xFFL).toByte()
        }
    }

    fun close() {
        controlJob?.cancel()
        videoJob?.cancel()
        runCatching { controlListener.close() }
        runCatching { udpSocket.close() }
    }
}
