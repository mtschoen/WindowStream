package com.mtschoen.windowstream.viewer.discovery

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.assertThrows

class TextRecordParserTest {
    @Test
    fun `parses all three expected keys`() {
        val records: Map<String, ByteArray> = mapOf(
            "version" to "1".toByteArray(),
            "hostname" to "mtsch-desktop".toByteArray(),
            "protocolRev" to "1".toByteArray()
        )
        val parsed: ParsedTextRecord = TextRecordParser.parse(records)
        assertEquals(1, parsed.protocolMajorVersion)
        assertEquals(1, parsed.protocolMinorRevision)
        assertEquals("mtsch-desktop", parsed.hostname)
    }

    @Test
    fun `missing version throws`() {
        assertThrows<MalformedTextRecordException> {
            TextRecordParser.parse(mapOf("hostname" to "x".toByteArray(), "protocolRev" to "1".toByteArray()))
        }
    }

    @Test
    fun `non-numeric version throws`() {
        assertThrows<MalformedTextRecordException> {
            TextRecordParser.parse(mapOf(
                "version" to "abc".toByteArray(),
                "hostname" to "x".toByteArray(),
                "protocolRev" to "1".toByteArray()
            ))
        }
    }
}
