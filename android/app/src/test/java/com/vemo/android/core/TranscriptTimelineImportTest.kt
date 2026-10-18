package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.nio.file.Files

class TranscriptTimelineImportTest {
    @Test fun sidecarCanArriveAfterLegacyResultAndSurvivesRestart() {
        val dir = Files.createTempDirectory("vemo-timeline").toFile()
        try {
            val store = RecordingStore(dir)
            val item = store.create("Memo", Mode.DESKTOP, "11234567-89ab-4cde-8fab-0123456789ab")
            store.partial(item.id).writeText("audio")
            store.transition(item.id, CaptureState.FINALIZING)
            val record = store.finish(item.id, 4000)
            val transcript = "첫 문장. 다음 문장."
            val bytes = Json.bytes(mapOf("schema_version" to 1, "recording_id" to record.id,
                "audio_sha256" to record.hash, "processor_id" to record.targetProcessor, "title" to "Memo",
                "recorded_at" to record.recordedAt, "transcript" to transcript, "notes_markdown" to "notes"))
            val cloud = MemoryCloud()
            val path = "results/${record.id}/${record.hash}"
            cloud.files["$path/result.json"] = bytes
            val engine = SyncEngine(cloud)
            val result = engine.result(record)!!
            store.saveResult(record, result.second)
            assertNull(engine.timeline(record, result.first))
            assertNull(store.result(record)!!.timeline)
            cloud.files["$path/${TranscriptTimeline.FILE_NAME}"] = Json.bytes(mapOf("schema_version" to 1,
                "recording_id" to record.id, "audio_sha256" to record.hash, "processor_id" to record.targetProcessor,
                "transcript_sha256" to digest(ByteArrayInputStream(transcript.toByteArray(Charsets.UTF_8))),
                "segments" to listOf(mapOf("start" to 1.25, "end" to 2, "text" to "첫 문장. "),
                    mapOf("start" to null, "end" to null, "text" to "다음 문장."))))
            val reloaded = engine.result(record)!!
            store.saveResult(record, reloaded.second)
            store.saveTimeline(record, engine.timeline(record, reloaded.first)!!)
            assertEquals(1250L, RecordingStore(dir).result(record)!!.timeline!!.first().startMs)
            assertArrayEquals(bytes, java.io.File(store.audio(record.id).parentFile, "result.json").readBytes())
            cloud.files["$path/${TranscriptTimeline.FILE_NAME}"] = "broken".toByteArray()
            assertNull(engine.timeline(record, result.first))
            assertEquals(transcript, engine.result(record)!!.first.transcript)
            java.io.File(store.audio(record.id).parentFile, TranscriptTimeline.FILE_NAME).writeText("broken")
            assertNull(store.result(record)!!.timeline)
            assertEquals(transcript, store.result(record)!!.transcript)
        } finally { dir.deleteRecursively() }
    }
}
