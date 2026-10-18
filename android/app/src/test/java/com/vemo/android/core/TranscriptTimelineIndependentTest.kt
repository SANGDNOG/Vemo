package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream

/** Independent wire adversaries; deliberately authored separately from the producer tests. */
class TranscriptTimelineIndependentTest {
    private val binding = Binding("01234567-89ab-4cde-8fab-0123456789ab", "a".repeat(64), "11234567-89ab-4cde-8fab-0123456789ab")
    private val transcript = "cafe\u0301 🎙️.\r\n한글!  "
    private fun sidecar(): ByteArray = Json.bytes(mapOf(
        "schema_version" to 1, "recording_id" to binding.id, "audio_sha256" to binding.hash,
        "processor_id" to binding.processor,
        "transcript_sha256" to digest(ByteArrayInputStream(transcript.toByteArray(Charsets.UTF_8))),
        "segments" to listOf(mapOf("start" to 0.0, "end" to 2.0, "text" to transcript))))
    private fun parse(bytes: ByteArray) = TranscriptTimeline.parse(bytes, binding, transcript, 2000)

    @Test fun exactUtf8TextAndBoundaryTimesArePreserved() {
        val rows = parse(sidecar())
        assertEquals(transcript, rows.joinToString("") { it.text })
        assertEquals(0L, rows.single().startMs)
        val normalized = transcript.replace("e\u0301", "é")
        assertThrows(ProtocolException::class.java) { TranscriptTimeline.parse(sidecar(), binding, normalized, 2000) }
    }
    @Test fun rejectsNonfiniteAndNonnumericJsonTiming() {
        val source = sidecar().toString(Charsets.UTF_8)
        for (value in listOf("1e9999", "-1e9999", "NaN", "Infinity", "true", "[]", "{}", "\"0\"", "null")) {
            val bytes = source.replace("\"start\":0.0", "\"start\":$value").toByteArray(Charsets.UTF_8)
            assertFalse("Fixture replacement must apply", source == bytes.toString(Charsets.UTF_8))
            assertThrows("Rejected $value", ProtocolException::class.java) { parse(bytes) }
        }
    }
    @Test fun rejectsDuplicateEscapedKeysUnknownFieldsAndMalformedUtf8() {
        val source = sidecar().toString(Charsets.UTF_8)
        for (bytes in listOf(
            (source.dropLast(1) + ",\"schema_version\":1}").toByteArray(),
            source.replace("\"start\":0.0", "\"start\":0.0,\"st\\u0061rt\":0.0").toByteArray(),
            (source.dropLast(1) + ",\"extra\":null}").toByteArray(),
            source.replace("\"end\":2.0", "\"end\":2.0,\"extra\":null").toByteArray(),
            source.replace("\"schema_version\":1", "\"schema_version\":true").toByteArray(),
            byteArrayOf(0x7b, 0xc3.toByte(), 0x28, 0x7d)
        )) assertThrows(ProtocolException::class.java) { parse(bytes) }
    }
    @Test fun plainFallbackPreservesSupplementaryCharactersAndWhitespace() {
        for (source in listOf(transcript, "🎙️🧑🏽‍💻 ".repeat(400), "\r\n \t", "", "같은 말. 같은 말.\n같은 말.")) {
            val rows = TranscriptTimeline.plain(source)
            assertEquals(source, rows.joinToString("") { it.text })
            assertTrue(rows.all { it.startMs == null && it.timestamp == null })
            assertTrue(rows.all { it.text.codePointCount(0, it.text.length) <= 320 })
        }
    }

    @Test fun repeatedDownloadPreservesBytesAndChangedTranscriptDisablesStaleSidecar() {
        val dir = java.nio.file.Files.createTempDirectory("timeline-independent").toFile()
        try {
            val store = RecordingStore(dir)
            val created = store.create("Memo", Mode.DESKTOP, binding.processor)
            store.partial(created.id).writeText("independent audio")
            store.transition(created.id, CaptureState.FINALIZING)
            val record = store.finish(created.id, 2000)
            fun resultBytes(text: String) = Json.bytes(mapOf("schema_version" to 1,
                "recording_id" to record.id, "audio_sha256" to record.hash, "processor_id" to record.targetProcessor,
                "title" to "Memo", "recorded_at" to record.recordedAt, "transcript" to text, "notes_markdown" to "notes"))
            val timeline = Json.parse(sidecar()).apply {
                addProperty("recording_id", record.id); addProperty("audio_sha256", record.hash)
            }.let { Json.bytes(it) }
            val cloud = MemoryCloud()
            val base = "results/${record.id}/${record.hash}"
            val original = resultBytes(transcript)
            cloud.files["$base/result.json"] = original
            cloud.files["$base/${TranscriptTimeline.FILE_NAME}"] = timeline
            val engine = SyncEngine(cloud)
            repeat(2) {
                val downloaded = engine.result(record)!!
                store.saveResult(record, downloaded.second)
                store.saveTimeline(record, engine.timeline(record, downloaded.first)!!)
                assertArrayEquals(original, java.io.File(store.audio(record.id).parentFile, "result.json").readBytes())
                assertArrayEquals(timeline, java.io.File(store.audio(record.id).parentFile, TranscriptTimeline.FILE_NAME).readBytes())
            }
            val invalid = Json.parse(timeline).apply { addProperty("processor_id", binding.id) }.let { Json.bytes(it) }
            assertThrows(ProtocolException::class.java) { store.saveTimeline(record, invalid) }
            assertArrayEquals(timeline, java.io.File(store.audio(record.id).parentFile, TranscriptTimeline.FILE_NAME).readBytes())
            val invalidTiming = timeline.toString(Charsets.UTF_8).replace("\"end\":2.0", "\"end\":2000.0").toByteArray()
            assertFalse(timeline.contentEquals(invalidTiming))
            cloud.files["$base/${TranscriptTimeline.FILE_NAME}"] = invalidTiming
            assertNull(engine.timeline(record, engine.result(record)!!.first))
            val sidecarFile = java.io.File(store.audio(record.id).parentFile, TranscriptTimeline.FILE_NAME)
            sidecarFile.writeBytes(invalidTiming)
            assertEquals(transcript, RecordingStore(dir).result(record)!!.transcript)
            assertNull(RecordingStore(dir).result(record)!!.timeline)
            sidecarFile.writeBytes(timeline)
            val changed = resultBytes("new transcript")
            cloud.files["$base/result.json"] = changed
            val updated = engine.result(record)!!
            assertNull(engine.timeline(record, updated.first))
            store.saveResult(record, updated.second)
            val fresh = RecordingStore(dir).result(record)!!
            assertEquals("new transcript", fresh.transcript)
            assertNull(fresh.timeline)
            assertEquals(record.hash, sha256(store.audio(record.id)))
        } finally { dir.deleteRecursively() }
    }
    @Test fun optionalTimelineIoFailureFallsBackButCancellationPropagates() {
        val record = Recording(binding.id, "Memo", "2026-10-17T17:00:00+09:00", Mode.DESKTOP,
            binding.processor, CaptureState.COMPLETE, hash = binding.hash, bytes = 10, durationMs = 2000)
        val result = Result("Memo", record.recordedAt, transcript, "notes")
        fun engine(failure: java.io.IOException) = SyncEngine(object : CloudStorage {
            override fun ensureFolders(path: String) = Unit
            override fun read(path: String, limit: Int): ByteArray? { throw failure }
            override fun putImmutable(path: String, size: Long, mime: String, source: () -> java.io.InputStream) = Unit
        })
        assertNull(engine(java.io.IOException("offline")).timeline(record, result))
        assertThrows(java.io.InterruptedIOException::class.java) {
            engine(java.io.InterruptedIOException("cancelled")).timeline(record, result)
        }
    }
}
