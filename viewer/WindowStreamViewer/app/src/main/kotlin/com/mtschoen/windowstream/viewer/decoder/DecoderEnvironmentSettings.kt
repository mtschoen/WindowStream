package com.mtschoen.windowstream.viewer.decoder

internal data class DecoderEnvironmentSettings(val lowLatencyMode: Int) {
    companion object {
        fun load(environmentValue: String?): DecoderEnvironmentSettings {
            val lowLatencyMode = when (environmentValue) {
                "0" -> 0
                else -> 1
            }
            return DecoderEnvironmentSettings(lowLatencyMode)
        }
    }
}
