package com.mtschoen.windowstream.viewer.decoder

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test

class DecoderEnvironmentSettingsTest {
    @Test
    fun `load keeps low latency enabled when variable is absent`() {
        val settings = DecoderEnvironmentSettings.load(null)

        assertEquals(1, settings.lowLatencyMode)
    }

    @Test
    fun `load disables low latency for zero override`() {
        val settings = DecoderEnvironmentSettings.load("0")

        assertEquals(0, settings.lowLatencyMode)
    }

    @Test
    fun `load keeps low latency enabled for one override`() {
        val settings = DecoderEnvironmentSettings.load("1")

        assertEquals(1, settings.lowLatencyMode)
    }

    @Test
    fun `load ignores unsupported override`() {
        val settings = DecoderEnvironmentSettings.load("false")

        assertEquals(1, settings.lowLatencyMode)
    }
}
