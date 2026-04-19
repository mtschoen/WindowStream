package com.mtschoen.windowstream.viewer.discovery

import java.net.InetAddress

/**
 * Placeholder for Phase 7's ServerInformation.
 * The post-merge integration step will replace this with the canonical
 * implementation from the discovery package built in Phase 7.
 */
data class ServerInformation(
    val hostname: String,
    val host: InetAddress,
    val controlPort: Int,
    val protocolMajorVersion: Int,
    val protocolMinorRevision: Int
)
