package com.mtschoen.windowstream.viewer.demo

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Shares one server session among every stream targeting the same control endpoint.
 */
internal class ServerSessionRegistry<Session>(
    private val createSession: suspend (host: String, port: Int) -> Session
) {
    private data class Endpoint(val host: String, val port: Int)

    private val lock = Mutex()
    private val sessions: MutableMap<Endpoint, Session> = mutableMapOf()

    suspend fun getOrCreate(host: String, port: Int): Session = lock.withLock {
        val endpoint = Endpoint(host, port)
        sessions.getOrPut(endpoint) { createSession(host, port) }
    }
}
