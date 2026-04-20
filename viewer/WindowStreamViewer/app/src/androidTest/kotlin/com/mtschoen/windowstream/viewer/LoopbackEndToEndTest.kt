package com.mtschoen.windowstream.viewer

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.mtschoen.windowstream.viewer.app.ViewerPipeline
import com.mtschoen.windowstream.viewer.control.ControlClient
import com.mtschoen.windowstream.viewer.control.DisplayCapabilities
import com.mtschoen.windowstream.viewer.decoder.MediaCodecDecoder
import com.mtschoen.windowstream.viewer.decoder.TextureSink
import com.mtschoen.windowstream.viewer.discovery.ServerInformation
import com.mtschoen.windowstream.viewer.fakes.FakeWindowStreamServer
import com.mtschoen.windowstream.viewer.fakes.RecordedH264Stream
import com.mtschoen.windowstream.viewer.transport.UdpTransportReceiver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.net.InetAddress
import kotlin.time.Duration.Companion.seconds

/**
 * End-to-end loopback test that verifies the full viewer pipeline decodes and renders frames
 * from a pre-recorded H.264 Annex-B bitstream served by a local fake WindowStream server.
 *
 * The test:
 * 1. Loads a 60-frame 320×240 Annex-B stream from the instrumentation test assets.
 * 2. Starts a [FakeWindowStreamServer] on the loopback interface.
 * 3. Constructs a [ViewerPipeline] with [TextureSink] and a fixed-port UDP receiver
 *    so the fake server knows where to deliver packets.
 * 4. Asserts that at least 30 frames are decoded and reported via [TextureSink.framesRenderedCount]
 *    within a 30-second timeout.
 */
@RunWith(AndroidJUnit4::class)
class LoopbackEndToEndTest {

    /**
     * Fixed loopback UDP port used by the injected [UdpTransportReceiver].
     * Must not collide with other services; chosen from the ephemeral-safe range.
     */
    private val viewerUdpPort: Int = 47321

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

        val fakeServer = FakeWindowStreamServer(
            frames = frames,
            viewerUdpPort = viewerUdpPort,
            width = 320,
            height = 240,
            framesPerSecond = 60
        )
        fakeServer.start(scope)

        val sink = TextureSink()

        // Construct the pipeline with real Android platform factories but with a fixed-port
        // UdpTransportReceiver so FakeWindowStreamServer can deliver packets to it.
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
                // Bind to the fixed loopback port so the fake server knows where to send frames.
                UdpTransportReceiver(
                    bindAddress = InetAddress.getLoopbackAddress(),
                    requestedPort = viewerUdpPort
                )
            },
            mediaCodecDecoderFactory = { frameSink, onKeyframeRequested ->
                MediaCodecDecoder(frameSink = frameSink, onKeyframeRequested = onKeyframeRequested)
            }
        )

        val serverInformation = ServerInformation(
            hostname = "fake",
            host = InetAddress.getLoopbackAddress(),
            controlPort = fakeServer.controlPort,
            protocolMajorVersion = 1,
            protocolMinorRevision = 1
        )
        pipeline.connect(scope, serverInformation)

        withTimeout(30.seconds) {
            while (sink.framesRenderedCount.get() < 30) {
                delay(100)
            }
        }

        assertTrue(sink.framesRenderedCount.get() >= 30)
        pipeline.disconnect()
        fakeServer.close()
    }
}
