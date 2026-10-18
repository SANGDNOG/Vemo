package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.nio.file.Files

class ProtocolTest {
    private val id = "01234567-89ab-4cde-8fab-0123456789ab"
    private val processor = "11234567-89ab-4cde-8fab-0123456789ab"
    private val hash = "a".repeat(64)
    private fun result(id: String = this.id, hash: String = this.hash, processor: String = this.processor, date: String = "2026-10-17T17:00:00+09:00") =
        """{"schema_version":1,"recording_id":"$id","audio_sha256":"$hash","processor_id":"$processor","title":"제목","recorded_at":"$date","transcript":"전사","notes_markdown":"노트"}""".toByteArray()
    private val binding get() = Binding(id, hash, processor)

    @Test fun rejectsUnknownProcessorFields() {
        val bytes = """{"schema_version":1,"processor_id":"$processor","display_name":"Mac","extra":null}""".toByteArray()
        assertThrows(ProtocolException::class.java) { Wire.processor(bytes) }
    }
    @Test fun rejectsUnknownResultFields() {
        val bytes = (String(result(), Charsets.UTF_8).dropLast(1) + """, "extra":null}""").toByteArray()
        assertThrows(ProtocolException::class.java) { Wire.result(bytes, binding) }
    }
    @Test fun rejectsEveryMissingProcessorField() {
        val bytes = """{"schema_version":1,"processor_id":"$processor","display_name":"Mac"}""".toByteArray()
        assertEquals(Processor(processor, "Mac"), Wire.processor(bytes))
        for (key in listOf("schema_version", "processor_id", "display_name")) {
            val obj = Json.parse(bytes).apply { remove(key) }
            assertThrows(ProtocolException::class.java) { Wire.processor(Json.bytes(obj)) }
        }
    }
    @Test fun rejectsEveryMissingResultField() {
        for (key in listOf("schema_version", "recording_id", "audio_sha256", "processor_id", "title", "recorded_at", "transcript", "notes_markdown")) {
            val obj = Json.parse(result()).apply { remove(key) }
            assertThrows(ProtocolException::class.java) { Wire.result(Json.bytes(obj), binding) }
        }
    }
    @Test fun rejectsStaleResultsAndWrongProcessor() {
        assertEquals("전사", Wire.result(result(), binding).transcript)
        for (bytes in listOf(result(id = processor), result(hash = "b".repeat(64)), result(processor = id))) {
            assertThrows(ProtocolException::class.java) { Wire.result(bytes, binding) }
        }
    }
    @Test fun rejectsNonCanonicalUuidHashDateAndPartialMarkers() {
        assertThrows(ProtocolException::class.java) { Wire.result(result(id = id.uppercase()), binding) }
        assertThrows(ProtocolException::class.java) { Wire.result(result(date = "2026-02-30T17:00:00+09:00"), binding) }
        assertThrows(ProtocolException::class.java) { Wire.result(result(date = "2026-10-17T17:00:00"), binding) }
        assertThrows(ProtocolException::class.java) { Wire.processor("""{"schema_version":2,"processor_id":"$processor","display_name":"Mac"}""".toByteArray()) }
        assertThrows(ProtocolException::class.java) { Wire.processor("""{"schema_version":1,"processor_id":"$processor","processor_id":"$id","display_name":"Mac"}""".toByteArray()) }
        val duplicateResult = (String(result(), Charsets.UTF_8).dropLast(1) + """, "title":"duplicate"}""").toByteArray()
        assertThrows(ProtocolException::class.java) { Wire.result(duplicateResult, binding) }
        assertThrows(ProtocolException::class.java) { Wire.result(byteArrayOf(0xc3.toByte(), 0x28), binding) }
    }
    @Test fun rejectsDatesOutsideSharedContract() {
        for (date in listOf("2026-10-17T17:00:00+14:01", "2026-10-17T17:00:00+18:00", "0000-10-17T17:00:00Z")) {
            assertThrows(ProtocolException::class.java) { validDate(date) }
        }
        assertEquals("2026-10-17T17:00:00+14:00", validDate("2026-10-17T17:00:00+14:00"))
        val record = Recording(id, "title", "2026-10-17T17:00:00Z", Mode.RECORD_ONLY, null, CaptureState.COMPLETE, hash, 16L * 1024 * 1024 * 1024 + 1, 10)
        assertThrows(ProtocolException::class.java) { Wire.marker(record) }
    }
    @Test fun boundsRemoteJsonBeforeParsing() {
        assertThrows(ProtocolException::class.java) { readBounded(ByteArrayInputStream(ByteArray(8 * 1024 * 1024 + 1)), Wire.MAX_JSON) }
        assertEquals(8 * 1024 * 1024, readBounded(ByteArrayInputStream(ByteArray(8 * 1024 * 1024)), Wire.MAX_JSON).size)
    }
    @Test fun emitsExactCompletedMarkerAfterAudio() {
        val file = Files.createTempFile("vemo", ".m4a").toFile().apply { writeText("audio") }
        try {
            val record = Recording(id, "녹음", "2026-10-17T17:00:00+09:00", Mode.DESKTOP, processor,
                CaptureState.COMPLETE, sha256(file), file.length(), 1000)
            val cloud = MemoryCloud()
            SyncEngine(cloud).upload(record, file)
            assertEquals(listOf("recordings/$id/${record.hash}/audio.m4a", "recordings/$id/${record.hash}/recording.json"), cloud.writes)
            val marker = String(cloud.files.getValue(cloud.writes.last()), Charsets.UTF_8)
            assertTrue(marker.contains("\"audio_file\":\"audio.m4a\""))
            assertTrue(marker.contains("\"completed\":true"))
            assertTrue(marker.contains("\"mode\":\"desktop\""))
            assertFalse(marker.contains("password"))
        } finally { file.delete() }
    }
    @Test fun failedAudioNeverPublishesMarkerAndChangedLocalAudioIsRejected() {
        val file = Files.createTempFile("vemo", ".m4a").toFile().apply { writeText("audio") }
        try {
            val record = Recording(id, "title", "2026-10-17T17:00:00+09:00", Mode.DESKTOP, processor, CaptureState.COMPLETE, sha256(file), 5, 10)
            val cloud = MemoryCloud(failAudio = true)
            assertThrows(Exception::class.java) { SyncEngine(cloud).upload(record, file) }
            assertFalse(cloud.files.keys.any { it.endsWith("recording.json") })
            file.writeText("other")
            assertThrows(ProtocolException::class.java) { SyncEngine(MemoryCloud()).upload(record, file) }
        } finally { file.delete() }
    }
    @Test fun rejectsPausedOrUnboundDesktopUploads() {
        val file = Files.createTempFile("vemo", ".m4a").toFile().apply { writeText("audio") }
        try {
            for (state in listOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.INTERRUPTED, CaptureState.FINALIZING)) {
                val record = Recording(id, "title", "2026-10-17T17:00:00+09:00", Mode.DESKTOP, processor, state, sha256(file), 5, 10)
                assertThrows(ProtocolException::class.java) { SyncEngine(MemoryCloud()).upload(record, file) }
            }
            val unbound = Recording(id, "title", "2026-10-17T17:00:00+09:00", Mode.DESKTOP, null, CaptureState.COMPLETE, sha256(file), 5, 10)
            assertThrows(ProtocolException::class.java) { SyncEngine(MemoryCloud()).upload(unbound, file) }
        } finally { file.delete() }
    }
    @Test fun rejectsTraversalEncodedPathsAndUnsafeEndpoints() {
        for (path in listOf("../x", "a/../x", "/root", "a//b", "a/%2e%2e", "a\\b", "a/.", "a?query")) {
            assertThrows(ProtocolException::class.java) { safeRelativePath(path) }
        }
        assertEquals(listOf("Vemo", "한국어 녹음"), safeRelativePath("Vemo/한국어 녹음"))
        for (url in listOf("http://example.com", "https://u:p@example.com/dav", "https://example.com/dav?x=1", "https://example.com/dav#x")) {
            assertThrows(ProtocolException::class.java) { DavConfig(url, "Vemo").validate() }
        }
    }
}

internal class MemoryCloud(private val failAudio: Boolean = false) : CloudStorage {
    val files = mutableMapOf<String, ByteArray>()
    val writes = mutableListOf<String>()
    override fun ensureFolders(path: String) { safeRelativePath(path) }
    override fun read(path: String, limit: Int): ByteArray? = files[path]?.also { if (it.size > limit) throw ProtocolException("Too large") }
    override fun putImmutable(path: String, size: Long, mime: String, source: () -> java.io.InputStream) {
        if (failAudio && path.endsWith("audio.m4a")) throw java.io.IOException("Interrupted upload")
        val bytes = source().use { it.readBytes() }
        if (files[path] != null && !files.getValue(path).contentEquals(bytes)) throw ConflictException()
        files[path] = bytes
        writes += path
    }
}
