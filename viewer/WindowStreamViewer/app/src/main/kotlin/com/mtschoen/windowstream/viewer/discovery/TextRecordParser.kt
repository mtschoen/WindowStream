package com.mtschoen.windowstream.viewer.discovery

class MalformedTextRecordException(message: String) : RuntimeException(message)

data class ParsedTextRecord(
    val protocolMajorVersion: Int,
    val protocolMinorRevision: Int,
    val hostname: String
)

object TextRecordParser {
    fun parse(records: Map<String, ByteArray>): ParsedTextRecord {
        val versionString: String = records["version"]?.toString(Charsets.UTF_8)
            ?: throw MalformedTextRecordException("missing version")
        val revisionString: String = records["protocolRev"]?.toString(Charsets.UTF_8)
            ?: throw MalformedTextRecordException("missing protocolRev")
        val hostname: String = records["hostname"]?.toString(Charsets.UTF_8)
            ?: throw MalformedTextRecordException("missing hostname")
        val majorVersion: Int = versionString.toIntOrNull()
            ?: throw MalformedTextRecordException("version not numeric: $versionString")
        val minorRevision: Int = revisionString.toIntOrNull()
            ?: throw MalformedTextRecordException("protocolRev not numeric: $revisionString")
        return ParsedTextRecord(majorVersion, minorRevision, hostname)
    }
}
