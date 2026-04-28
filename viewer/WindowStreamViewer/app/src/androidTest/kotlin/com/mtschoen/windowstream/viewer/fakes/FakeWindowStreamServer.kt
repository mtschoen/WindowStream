package com.mtschoen.windowstream.viewer.fakes

import android.util.Log
import com.mtschoen.windowstream.viewer.control.ControlMessage
import com.mtschoen.windowstream.viewer.control.LengthPrefixFraming
import com.mtschoen.windowstream.viewer.control.ProtocolSerialization
import com.mtschoen.windowstream.viewer.control.WindowDescriptor
import com.mtschoen.windowstream.viewer.transport.PacketHeader
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import kotlin.time.Duration.Companion.seconds

/**
 * Minimal fake server that speaks the WindowStream control protocol over TCP and
 * sends a pre-recorded H.264 Annex-B stream over UDP to the viewer's UDP receive port.
 *
 * The viewer's UDP port is provided via [viewerUdpPortDeferred], which must be resolved by the
 * test before (or shortly after) [start] is called. The video job awaits this deferred before
 * sending any UDP frames, then waits an additional 500 ms to give the viewer's decode coroutine
 * time to subscribe to the [kotlinx.coroutines.flow.SharedFlow]. After that delay, NAL units are
 * sent in a continuous loop so the decoder always receives a fresh SPS + PPS + IDR access unit
 * on each pass, even if earlier passes arrived before [android.media.MediaCodec] finished
 * initialising its async callbacks. The sequence counter advances monotonically across passes to
 * keep presentation timestamps strictly increasing.
 *
 * The control coroutine sends periodic [ControlMessage.Heartbeat] messages so the viewer's
 * 6-second heartbeat-silence-timeout does not close the TCP control socket during the test.
 *
 * @param frames                Annex-B NAL unit byte arrays produced by [RecordedH264Stream].
 * @param viewerUdpPortDeferred Deferred resolved to the viewer's UDP receive port after the
 *                              viewer's [com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver]
 *                              has called [com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver.start].
 * @param streamId              Stream identifier reported in [ControlMessage.StreamStarted].
 * @param width                 Frame width in pixels.
 * @param height                Frame height in pixels.
 * @param framesPerSecond       Nominal frame rate used for presentation timestamps.
 */
class FakeWindowStreamServer(
    private val frames: List<ByteArray>,
    private val viewerUdpPortDeferred: CompletableDeferred<Int>,
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
            val input = client.getInputStream()
            val output = client.getOutputStream()
            val writeLock = Any()

            // Read the viewer's HELLO frame.
            LengthPrefixFraming.readFrame(input)

            // v2 ServerHello: advertise a single fake window. The udpPort field on
            // ServerHello is not used by ViewerPipeline.beginStreaming() — the viewer
            // always creates its own UDP receive socket at port 0 (OS-assigned).
            val fakeWindow = WindowDescriptor(
                windowId = 1uL,
                hwnd = 1L,
                processId = 0,
                processName = "fake",
                title = "fake-window",
                physicalWidth = width,
                physicalHeight = height
            )
            val serverHello: String = ProtocolSerialization.json.encodeToString(
                ControlMessage.serializer(),
                ControlMessage.ServerHello(
                    serverVersion = 2,
                    udpPort = 0,
                    windows = listOf(fakeWindow)
                )
            )
            synchronized(writeLock) {
                LengthPrefixFraming.writeFrame(output, serverHello.toByteArray(Charsets.UTF_8))
            }
            Log.i("FakeWindowStreamServer", "Sent ServerHello v2 (1 window, udpPort placeholder=0)")

            // Send periodic heartbeats so the viewer's 6-second silence-timeout does not close
            // the TCP socket before the test assertion completes.
            val heartbeatJob = launch(Dispatchers.IO) {
                val heartbeatPayload: ByteArray = ProtocolSerialization.json
                    .encodeToString(ControlMessage.serializer(), ControlMessage.Heartbeat)
                    .toByteArray(Charsets.UTF_8)
                while (isActive) {
                    delay(2_000)
                    runCatching {
                        synchronized(writeLock) {
                            LengthPrefixFraming.writeFrame(output, heartbeatPayload)
                        }
                    }
                }
            }

            // v2 flow: the viewer (DemoActivity) sends OPEN_STREAM after ServerHello. Reply
            // to the first inbound frame with STREAM_STARTED so DemoActivity's
            // withTimeout-on-StreamStarted unblocks. Subsequent frames (heartbeats, keyframe
            // requests) are drained without response.
            try {
                var streamStartedSent = false
                while (isActive) {
                    LengthPrefixFraming.readFrame(input) ?: break
                    if (!streamStartedSent) {
                        streamStartedSent = true
                        val streamStarted: String = ProtocolSerialization.json.encodeToString(
                            ControlMessage.serializer(),
                            ControlMessage.StreamStarted(
                                streamId = streamId,
                                windowId = fakeWindow.windowId,
                                codec = "h264",
                                width = width,
                                height = height,
                                framesPerSecond = framesPerSecond
                            )
                        )
                        synchronized(writeLock) {
                            LengthPrefixFraming.writeFrame(output, streamStarted.toByteArray(Charsets.UTF_8))
                        }
                        Log.i("FakeWindowStreamServer", "Sent StreamStarted v2 in response to first viewer frame")
                    }
                }
            } catch (ignored: Exception) {
                // Socket may close when the test calls fakeServer.close().
            } finally {
                heartbeatJob.cancel()
                runCatching { client.close() }
            }
        }

        videoJob = scope.launch(Dispatchers.IO) {
            // Wait for the viewer's UDP receiver to bind.
            val viewerUdpPort: Int = withTimeoutOrNull(10.seconds) {
                viewerUdpPortDeferred.await()
            } ?: error("Timed out waiting for viewer UDP port (video job)")

            // Extra pause so the decode coroutine — which is launched after receiveJob starts —
            // has time to subscribe to the SharedFlow before the first frame is emitted.
            // Without this, frames emitted before the collector subscribes are lost (replay=0).
            delay(500)

            // Use explicit IPv4 loopback so the datagram reaches the viewer's IPv4 DatagramSocket
            // (bound to 0.0.0.0). InetAddress.getLoopbackAddress() returns ::1 on API 36 which
            // is IPv6 and is dropped by an IPv4-only receiver socket.
            val targetAddress: InetAddress = InetAddress.getByName("127.0.0.1")

            // Stream the NAL units in a continuous loop so the decoder always receives a fresh
            // SPS + PPS + IDR keyframe sequence on each pass, even if the first pass was missed
            // because MediaCodec initialisation had not completed before the initial frames arrived.
            // Each pass advances the sequence counter to keep presentation timestamps monotonic.
            var sequence: Long = 0
            var pass = 0
            while (isActive) {
                Log.i("FakeWindowStreamServer", "Starting pass $pass — sending ${frames.size} NAL units to ${targetAddress.hostAddress}:$viewerUdpPort (sequence starts at $sequence)")
                for (nalBytes in frames) {
                    if (!isActive) break
                    emitFragmented(nalBytes, sequence, targetAddress, viewerUdpPort)
                    sequence++
                    delay(1_000L / framesPerSecond)
                }
                Log.i("FakeWindowStreamServer", "Finished pass $pass")
                pass++
            }
        }
    }

    private fun emitFragmented(nalBytes: ByteArray, sequence: Long, target: InetAddress, viewerUdpPort: Int) {
        val maximumPayload: Int = PacketHeader.MAXIMUM_PAYLOAD_BYTE_LENGTH
        val fragmentCount: Int = ((nalBytes.size + maximumPayload - 1) / maximumPayload).coerceAtLeast(1)
        if (sequence == 0L || sequence % 10 == 0L) {
            Log.i("FakeWindowStreamServer", "Sending sequence=$sequence nalSize=${nalBytes.size} fragments=$fragmentCount")
        }
        for (fragmentIndex in 0 until fragmentCount) {
            val start: Int = fragmentIndex * maximumPayload
            val end: Int = minOf(nalBytes.size, start + maximumPayload)
            val slice: ByteArray = nalBytes.copyOfRange(start, end)
            val isLast: Boolean = fragmentIndex == fragmentCount - 1
            // The NAL unit type is encoded in the low 5 bits of the first byte after the start code.
            // Handle both 4-byte (00 00 00 01) and 3-byte (00 00 01) Annex-B start codes.
            val isFourByteStartCode: Boolean = nalBytes.size > 3 &&
                nalBytes[0] == 0.toByte() && nalBytes[1] == 0.toByte() &&
                nalBytes[2] == 0.toByte() && nalBytes[3] == 1.toByte()
            val nalHeaderOffset: Int = if (isFourByteStartCode) 4 else 3
            val nalUnitType: Int = if (nalBytes.size > nalHeaderOffset) {
                nalBytes[nalHeaderOffset].toInt() and 0x1F
            } else {
                0
            }
            val isIdrFrame: Boolean = nalUnitType == 5
            val flags: Int = (if (isIdrFrame) PacketHeader.FLAG_IDR_FRAME else 0) or
                (if (isLast) PacketHeader.FLAG_LAST_FRAGMENT else 0)
            val datagram: ByteArray = buildPacket(sequence, flags, fragmentIndex, fragmentCount, slice)
            runCatching {
                udpSocket.send(DatagramPacket(datagram, datagram.size, target, viewerUdpPort))
            }
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
