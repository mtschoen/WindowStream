package com.mtschoen.windowstream.viewer.discovery

import java.net.InetAddress

data class ServerInformation(
    val hostname: String,
    val host: InetAddress,
    val controlPort: Int,
    val protocolMajorVersion: Int,
    val protocolMinorRevision: Int
)
