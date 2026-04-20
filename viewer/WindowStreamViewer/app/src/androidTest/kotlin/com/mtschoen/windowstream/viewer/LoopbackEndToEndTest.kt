package com.mtschoen.windowstream.viewer

import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.mtschoen.windowstream.viewer.app.ViewerPipeline
import com.mtschoen.windowstream.viewer.control.ControlClient
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.fakes.FakeWindowStreamServer
import com.mtschoen.windowstream.viewer.fakes.RecordedH264Stream
import com.mtschoen.windowstream.viewer.fakes.TestFrameSink
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import kotlin.time.Duration.Companion.seconds

/**
 * End-to-end loopback test that verifies the full viewer pipeline decodes and renders frames
 * from a pre-recorded H.264 Annex-B bitstream served by a local fake WindowStream server.
 *
 * Synchronisation (deferred-port handshake):
 *
 *  1. A [CompletableDeferred] is shared between the injected [UdpTransportReceiver] factory
 *     and [FakeWindowStreamServer].
 *  2. The [UdpTransportReceiver] is created with a custom [socketFactory] that completes the
 *     deferred with the OS-assigned port immediately after binding the socket. This happens
 *     synchronously inside [UdpTransportReceiver.start], before the flow is returned and before
 *     the decode coroutine subscribes.
 *  3. The fake server's video job awaits the deferred, then waits an additional 500 ms for the
 *     decode coroutine to subscribe to the [kotlinx.coroutines.flow.SharedFlow] before sending
 *     any UDP frames — ensuring no frames are dropped due to replay=0.
 */
@RunWith(AndroidJUnit4::class)
class LoopbackEndToEndTest {

    @Test
    fun textureSinkReportsFramesRenderedForRecordedH264() = runBlocking {
        val assetContext = InstrumentationRegistry.getInstrumentation().context
        val frames = assetContext.assets.open("sample-h264.bin").use { inputStream ->
            RecordedH264Stream.loadAnnexBFrames(inputStream)
        }
        assertTrue(
            "test asset should contain at least 30 NAL units, got ${frames.size}",
            frames.size >= 30
        )

        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

        // Resolved by the custom socketFactory after the UDP receive socket is bound.
        // FakeWindowStreamServer awaits this before sending any UDP frames.
        val viewerUdpPortDeferred: CompletableDeferred<Int> = CompletableDeferred()

        val fakeServer = FakeWindowStreamServer(
            frames = frames,
            viewerUdpPortDeferred = viewerUdpPortDeferred,
            width = 320,
            height = 240,
            framesPerSecond = 60
        )
        fakeServer.start(scope)

        val sink = TestFrameSink()

        val pipeline = ViewerPipeline(
            frameSink = sink,
            displayCapabilities = DisplayCapabilities(
                maximumWidth = 1920,
                maximumHeight = 1080,
                supportedCodecs = listOf("h264")
            ),
            controlClientFactory = { host, port, capabilities ->
                ControlClient(host = host, port = port, displayCapabilities = capabilities)
            },
            udpTransportReceiverFactory = { _, _ ->
                // Create UdpTransportReceiver with a custom socketFactory that:
                //  1. Binds explicitly to the IPv4 loopback address (127.0.0.1) so that datagrams
                //     sent by FakeWindowStreamServer to 127.0.0.1 are received. The default
                //     bindAddress from ViewerPipeline ("0.0.0.0") resolves to the IPv6 wildcard
                //     (::) on API 36, which does not receive IPv4 datagrams in this environment.
                //  2. Completes viewerUdpPortDeferred with the OS-assigned port immediately after
                //     binding, so FakeWindowStreamServer knows where to deliver frames.
                UdpTransportReceiver(
                    bindAddress = InetAddress.getByName("127.0.0.1"),
                    requestedPort = 0,
                    socketFactory = { address ->
                        val socket = DatagramSocket(address).also {
                            it.receiveBufferSize = 2 * 1024 * 1024
                        }
                        viewerUdpPortDeferred.complete(socket.localPort)
                        Log.i("LoopbackE2E", "UDP socket bound to port ${socket.localPort} on ${socket.localAddress}")
                        socket
                    }
                )
            },
            mediaCodecDecoderFactory = { frameSink, onKeyframeRequested ->
                // Use the software AVC decoder by name to avoid the goldfish hardware decoder's
                // async-mode callback bug (c2.goldfish.h264.decoder does not reliably deliver
                // onInputBufferAvailable after start() on API 36 managed device emulators).
                //
                // outputToSurface = false: configure the codec with a null output surface so that
                // decoded frames arrive as raw YUV buffer callbacks rather than being rendered to a
                // SurfaceTexture. This avoids the CCodecConfig "config failed => CORRUPTED" error
                // that occurs when any surface is passed to configure() on the API 36 emulator —
                // the Codec2 framework's surface-consumer-usage configuration fails, which prevents
                // onInputBufferAvailable callbacks from ever firing. Without a surface, the codec
                // operates in buffer-output mode and delivers callbacks normally.
                //
                // FrameSink.onFrameRendered() is still called from onOutputBufferAvailable in the
                // decoder, so framesRenderedCount is incremented for each decoded frame.
                MediaCodecDecoder(
                    frameSink = frameSink,
                    onKeyframeRequested = onKeyframeRequested,
                    codecName = "c2.android.avc.decoder",
                    outputToSurface = false
                )
            }
        )

        val serverInformation = ServerInformation(
            hostname = "fake",
            host = InetAddress.getLoopbackAddress(),
            controlPort = fakeServer.controlPort,
            protocolMajorVersion = 1,
            protocolMinorRevision = 1
        )
        Log.i("LoopbackE2E", "Connecting pipeline to fake server on control port ${fakeServer.controlPort}")
        pipeline.connect(scope, serverInformation)

        Log.i("LoopbackE2E", "Pipeline connected. Waiting for 30 decoded frames...")
        withTimeout(30.seconds) {
            var lastLogged = 0L
            while (sink.framesRenderedCount.get() < 30) {
                val now = System.currentTimeMillis()
                if (now - lastLogged >= 2000) {
                    Log.i("LoopbackE2E", "framesRenderedCount=${sink.framesRenderedCount.get()}")
                    lastLogged = now
                }
                delay(100)
            }
        }

        Log.i("LoopbackE2E", "Done. framesRenderedCount=${sink.framesRenderedCount.get()}")
        assertTrue(sink.framesRenderedCount.get() >= 30)
        pipeline.disconnect()
        fakeServer.close()
    }
}
