package com.mtschoen.windowstream.viewer.demo

import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotSame
import org.junit.jupiter.api.Assertions.assertSame
import org.junit.jupiter.api.Test
import java.util.concurrent.atomic.AtomicInteger

class ServerSessionRegistryTest {
    @Test
    fun `concurrent streams to one endpoint share one session`() = runBlocking {
        val creationCount = AtomicInteger()
        val registry = ServerSessionRegistry<Any> { _, _ ->
            creationCount.incrementAndGet()
            Any()
        }

        val sessions = List(3) {
            async { registry.getOrCreate("192.168.50.75", 65161) }
        }.awaitAll()

        assertEquals(1, creationCount.get())
        assertSame(sessions[0], sessions[1])
        assertSame(sessions[0], sessions[2])
    }

    @Test
    fun `different endpoints use different sessions`() = runBlocking {
        val registry = ServerSessionRegistry<Any> { _, _ -> Any() }

        val first = registry.getOrCreate("192.168.50.75", 65161)
        val second = registry.getOrCreate("192.168.50.76", 65161)

        assertNotSame(first, second)
    }
}
