package com.mtschoen.windowstream.viewer.control

import kotlinx.serialization.json.Json

object ProtocolSerialization {
    val json: Json = Json {
        classDiscriminator = "type"
        ignoreUnknownKeys = false
        encodeDefaults = true
    }
}
