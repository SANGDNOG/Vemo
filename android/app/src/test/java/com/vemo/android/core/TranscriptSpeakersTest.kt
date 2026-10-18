package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.InterruptedIOException
import java.nio.file.Files

class TranscriptSpeakersTest {
    private val binding = Binding("01234567-89ab-4cde-8fab-0123456789ab", "a".repeat(64), "11234567-89ab-4cde-8fab-0123456789ab")
    private val transcript = "첫 문장. 다음 문장!"
    private fun document(hash: String = binding.hash) = Json.parse(Json.bytes(mapOf(
        "schema_version" to 1, "audio_sha256" to hash,
        "transcript_sha256" to digest(ByteArrayInputStream(transcript.toByteArray(Charsets.UTF_8))),
        "generation" to binding.id, "speakers" to listOf(mapOf("id" to "speaker_1", "name" to "민수")),
        "turns" to listOf(mapOf("start_s" to 0, "end_s" to 2, "speaker_id" to "speaker_1")),
        "segments" to listOf(mapOf("start_s" to 0, "end_s" to 2, "text" to "첫 문장. ", "speaker_id" to "speaker_1"),
            mapOf("start_s" to 2, "end_s" to 4, "text" to "다음 문장!", "speaker_id" to null)))))
    private fun parse(bytes: ByteArray) = TranscriptSpeakers.parse(bytes, binding.hash, transcript, 4000)

    @Test fun displaysDesktopNamesAndUnassignedTextWithoutChangingTranscript() {
        val rows = parse(Json.bytes(document()))
        assertEquals(listOf("민수", null), rows.map { it.speakerName })
        assertEquals(transcript, rows.joinToString("") { it.text })
        assertEquals(listOf(0L, 2000L), rows.map { it.startMs })
    }

    @Test fun exactRawUtf8HashAndAudioHashAreMandatory() {
        for (field in listOf("audio_sha256", "transcript_sha256")) {
            val obj = document().apply { addProperty(field, "b".repeat(64)) }
            assertThrows(ProtocolException::class.java) { parse(Json.bytes(obj)) }
        }
        assertThrows(ProtocolException::class.java) {
            TranscriptSpeakers.parse(Json.bytes(document()), binding.hash, "$transcript\n", 4000)
        }
    }

    @Test fun rejectsDuplicateKeysIdsReferencesAndChangedWords() {
        val source = Json.bytes(document()).toString(Charsets.UTF_8)
        assertThrows(ProtocolException::class.java) { parse(source.replace("\"start_s\":0", "\"start_s\":0,\"st\\u0061rt_s\":0").toByteArray()) }
        for (change in listOf<(com.google.gson.JsonObject) -> Unit>(
            { it.getAsJsonArray("speakers").add(it.getAsJsonArray("speakers")[0].deepCopy()) },
            { it.getAsJsonArray("segments")[0].asJsonObject.addProperty("speaker_id", "missing") },
            { it.getAsJsonArray("turns")[0].asJsonObject.addProperty("speaker_id", "missing") },
            { it.getAsJsonArray("segments")[0].asJsonObject.addProperty("text", "다른 말") },
            { it.addProperty("generation", "not-a-uuid") }
        )) assertThrows(ProtocolException::class.java) { parse(Json.bytes(document().also(change))) }
    }

    @Test fun rejectsInvalidTimesInBothCollections() {
        for (collection in listOf("segments", "turns")) for (value in listOf("-1", "5", "3601", "1e9999", "\"0\"", "null")) {
            val obj = document()
            obj.getAsJsonArray(collection)[0].asJsonObject.add("start_s", com.google.gson.JsonParser.parseString(value))
            assertThrows("$collection $value", ProtocolException::class.java) { parse(Json.bytes(obj)) }
        }
    }

    @Test fun namesCountUnicodeScalarsAndRejectControlsOrUnpairedSurrogates() {
        for (name in listOf("🎙".repeat(80), "화자 1")) {
            val obj = document().apply { getAsJsonArray("speakers")[0].asJsonObject.addProperty("name", name) }
            assertEquals(name, parse(Json.bytes(obj))[0].speakerName)
        }
        for (name in listOf("", " 민수", "민수 ", "🎙".repeat(81), "민\n수", "a\u0085b", "a\u200Bb")) {
            val obj = document().apply { getAsJsonArray("speakers")[0].asJsonObject.addProperty("name", name) }
            assertThrows(ProtocolException::class.java) { parse(Json.bytes(obj)) }
        }
        // Use an escaped JSON scalar: UTF-8 encoding a JVM lone surrogate would replace it with '?'.
        val escaped = Json.bytes(document()).toString(Charsets.UTF_8).replace("민수", "\\ud800")
        assertThrows(ProtocolException::class.java) { parse(escaped.toByteArray(Charsets.UTF_8)) }
    }

    @Test fun coverageUsesDesktopNfcWhitespaceAndPunctuationNormalization() {
        val raw = "cafe\u0301.\r\n한글!"
        val obj = document().apply {
            addProperty("transcript_sha256", digest(ByteArrayInputStream(raw.toByteArray(Charsets.UTF_8))))
            getAsJsonArray("segments")[0].asJsonObject.addProperty("text", "café ")
            getAsJsonArray("segments")[1].asJsonObject.addProperty("text", "한글")
        }
        assertEquals(raw, TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, raw, 4000).joinToString("") { it.text })
        assertThrows(ProtocolException::class.java) {
            TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, "café.\r\n한글!", 4000)
        }
    }

    @Test fun rejectsOversizedDocumentsAndSegmentOrTurnCounts() {
        assertThrows(ProtocolException::class.java) { parse(ByteArray(Wire.MAX_JSON + 1) { 32 }) }
        for (collection in listOf("segments", "turns")) {
            val obj = document()
            val array = com.google.gson.JsonArray()
            repeat(100001) { array.add(com.google.gson.JsonNull.INSTANCE) }
            obj.add(collection, array)
            assertThrows(ProtocolException::class.java) { parse(Json.bytes(obj)) }
        }
    }

    @Test fun rejectsBackwardSegmentStartOrEndEvenWithRepeatedWords() {
        for ((start, end) in listOf(0.5 to 3.0, 1.5 to 1.8)) {
            val obj = document()
            val segments = obj.getAsJsonArray("segments")
            segments[0].asJsonObject.apply { addProperty("start_s", 1); addProperty("end_s", 2); addProperty("speaker_id", null as String?) }
            segments[1].asJsonObject.apply { addProperty("start_s", start); addProperty("end_s", end) }
            segments.forEach { it.asJsonObject.addProperty("text", "같은 말.") }
            val raw = "같은 말.같은 말."
            obj.addProperty("transcript_sha256", digest(ByteArrayInputStream(raw.toByteArray(Charsets.UTF_8))))
            assertThrows(ProtocolException::class.java) { TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, raw, 4000) }
        }
    }

    @Test fun assignmentRequiresStrictMajorityAndNoCompetingCoverage() {
        fun sidecar(turns: List<Map<String, Any>>) = document().apply {
            getAsJsonArray("speakers").add(Json.gson.toJsonTree(mapOf("id" to "speaker_2", "name" to "화자 2")))
            add("turns", Json.gson.toJsonTree(turns))
        }
        fun turn(start: Double, end: Double, id: String = "speaker_1"): Map<String, Any> =
            mapOf("start_s" to start, "end_s" to end, "speaker_id" to id)
        for (turns in listOf(emptyList(), listOf(turn(0.0, 1.0)),
            listOf(turn(0.0, 0.6), turn(0.4, 1.0)), // same-speaker overlap must not double-count
            listOf(turn(0.0, 2.0), turn(0.1, 0.2, "speaker_2")),
            listOf(turn(0.0, 1.8), turn(1.8, 1.9, "speaker_2")))) {
            val obj = sidecar(turns)
            assertThrows(ProtocolException::class.java) { parse(Json.bytes(obj)) }
            obj.getAsJsonArray("segments")[0].asJsonObject.addProperty("speaker_id", null as String?)
            assertNull(parse(Json.bytes(obj))[0].speakerName)
        }
        assertEquals("민수", parse(Json.bytes(sidecar(listOf(turn(0.0, 1.1)))))[0].speakerName)
        assertEquals("민수", parse(Json.bytes(sidecar(listOf(turn(0.0, 1.2), turn(0.5, 2.0)))))[0].speakerName)
    }

    @Test fun assignmentComplexityIsBoundedForRepeatedOverlappingSegments() {
        val obj = document()
        val turns = com.google.gson.JsonArray()
        repeat(2001) { index -> turns.add(Json.gson.toJsonTree(mapOf("start_s" to index / 1000.0,
            "end_s" to (index + 1) / 1000.0, "speaker_id" to "speaker_1"))) }
        val segments = com.google.gson.JsonArray()
        repeat(1000) { segments.add(Json.gson.toJsonTree(mapOf("start_s" to 0, "end_s" to 3,
            "text" to "말", "speaker_id" to "speaker_1"))) }
        obj.add("turns", turns); obj.add("segments", segments)
        val raw = "말".repeat(1000)
        obj.addProperty("transcript_sha256", digest(ByteArrayInputStream(raw.toByteArray(Charsets.UTF_8))))
        val error = assertThrows(ProtocolException::class.java) {
            TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, raw, 4000)
        }
        assertTrue(error.message.orEmpty().contains("complexity"))
    }

    @Test fun secondsUseStoredMillisecondsWithStrictDurationBoundary() {
        val obj = document()
        obj.getAsJsonArray("segments")[1].asJsonObject.addProperty("end_s", 4.0001)
        assertThrows(ProtocolException::class.java) { parse(Json.bytes(obj)) }
        assertEquals(2, TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, transcript, 4001).size)
    }

    @Test fun wordSegmentsBecomeSentenceRowsWithoutCrossingSpeakerChanges() {
        val raw = "첫 번째 문장입니다. 다음 문장입니다! 다른 화자. 미지정 말."
        val pieces = listOf("첫 ", "번째 ", "문장입니다. ", "다음 ", "문장입니다! ", "다른 ", "화자. ", "미지정 ", "말.")
        val obj = document().apply {
            addProperty("transcript_sha256", digest(ByteArrayInputStream(raw.toByteArray(Charsets.UTF_8))))
            getAsJsonArray("speakers").add(Json.gson.toJsonTree(mapOf("id" to "speaker_2", "name" to "영희")))
            add("turns", Json.gson.toJsonTree(listOf(
                mapOf("start_s" to 0, "end_s" to 5, "speaker_id" to "speaker_1"),
                mapOf("start_s" to 5, "end_s" to 7, "speaker_id" to "speaker_2"))))
            add("segments", Json.gson.toJsonTree(pieces.mapIndexed { index, text -> mapOf(
                "start_s" to index, "end_s" to index + 1, "text" to text,
                "speaker_id" to if (index < 5) "speaker_1" else if (index < 7) "speaker_2" else null) }))
        }
        val rows = TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, raw, 9000)
        assertEquals(listOf("첫 번째 문장입니다. ", "다음 문장입니다! ", "다른 화자. ", "미지정 말."), rows.map { it.text })
        assertEquals(listOf("민수", "민수", "영희", null), rows.map { it.speakerName })
        assertEquals(listOf(0L, 3000L, 5000L, 7000L), rows.map { it.startMs })
        assertEquals(raw, rows.joinToString("") { it.text })
    }

    @Test fun sameNameDifferentIdsStillStartNewRowsAndUnpunctuatedWordsAreBounded() {
        val raw = "말 ".repeat(400)
        val obj = document().apply {
            addProperty("transcript_sha256", digest(ByteArrayInputStream(raw.toByteArray(Charsets.UTF_8))))
            getAsJsonArray("speakers").add(Json.gson.toJsonTree(mapOf("id" to "speaker_2", "name" to "민수")))
            add("turns", Json.gson.toJsonTree(listOf(
                mapOf("start_s" to 0, "end_s" to 2, "speaker_id" to "speaker_1"),
                mapOf("start_s" to 2, "end_s" to 4, "speaker_id" to "speaker_2"))))
            add("segments", Json.gson.toJsonTree((0 until 400).map { index -> mapOf("start_s" to index / 100.0,
                "end_s" to (index + 1) / 100.0, "text" to "말 ", "speaker_id" to if (index < 200) "speaker_1" else "speaker_2") }))
        }
        val rows = TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, raw, 4000)
        assertEquals(raw, rows.joinToString("") { it.text })
        assertTrue(rows.all { it.text.codePointCount(0, it.text.length) <= 320 })
        assertTrue(rows.any { it.startMs == 2000L })
    }

    @Test fun projectsWordsOntoExactOriginalSpacesPunctuationAndNfc() {
        for ((raw, words) in listOf("안녕 세상." to listOf("안녕", "세상"),
            "cafe\u0301 세상!" to listOf("café", "세상"))) {
            val obj = document().apply {
                addProperty("transcript_sha256", digest(ByteArrayInputStream(raw.toByteArray(Charsets.UTF_8))))
                add("turns", Json.gson.toJsonTree(listOf(mapOf("start_s" to 0, "end_s" to 4, "speaker_id" to "speaker_1"))))
                add("segments", Json.gson.toJsonTree(words.mapIndexed { index, word -> mapOf(
                    "start_s" to index * 2, "end_s" to (index + 1) * 2, "text" to word, "speaker_id" to "speaker_1") }))
            }
            val rows = TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, raw, 4000)
            assertEquals(listOf(raw), rows.map { it.text })
            assertEquals(0L, rows.single().startMs)
        }
    }

    @Test fun sharedCrossPlatformWordFixturePreservesOriginalTextAndSpeakerBoundary() {
        // Mirrors local-validation/diarization/cross-platform.json without depending on a root-only file at test runtime.
        val raw = "안녕 세상. 다음 문장입니다."
        val obj = document().apply {
            addProperty("transcript_sha256", "ea57379f650ed059193db6a21b914dcf9d1c6ced09abd1814dbb4135863ec5c0")
            getAsJsonArray("speakers").add(Json.gson.toJsonTree(mapOf("id" to "speaker_2", "name" to "영희")))
            add("turns", Json.gson.toJsonTree(listOf(
                mapOf("start_s" to 0, "end_s" to 2, "speaker_id" to "speaker_1"),
                mapOf("start_s" to 2, "end_s" to 4, "speaker_id" to "speaker_2"))))
            add("segments", Json.gson.toJsonTree(listOf("안녕", "세상", "다음", "문장입니다").mapIndexed { index, word ->
                mapOf("start_s" to index, "end_s" to index + 1, "text" to word,
                    "speaker_id" to if (index < 2) "speaker_1" else "speaker_2") }))
        }
        val rows = TranscriptSpeakers.parse(Json.bytes(obj), binding.hash, raw, 4000)
        assertEquals(listOf("안녕 세상. ", "다음 문장입니다."), rows.map { it.text })
        assertEquals(listOf("민수", "영희"), rows.map { it.speakerName })
        assertEquals(listOf(0L, 2000L), rows.map { it.startMs })
        assertEquals(raw, rows.joinToString("") { it.text })
    }

    @Test fun optionalIoFailureFallsBackButCancellationPropagates() {
        val record = Recording(binding.id, "Memo", "2026-10-18T00:00:00Z", Mode.DESKTOP, binding.processor,
            CaptureState.COMPLETE, hash = binding.hash, bytes = 10, durationMs = 4000)
        val result = Result("Memo", record.recordedAt, transcript, "notes")
        fun engine(error: java.io.IOException) = SyncEngine(object : CloudStorage {
            override fun ensureFolders(path: String) = Unit
            override fun read(path: String, limit: Int): ByteArray? { throw error }
            override fun putImmutable(path: String, size: Long, mime: String, source: () -> java.io.InputStream) = Unit
        })
        assertNull(engine(java.io.IOException("offline")).speakers(record, result))
        assertThrows(InterruptedIOException::class.java) { engine(InterruptedIOException("cancelled")).speakers(record, result) }
    }

    @Test fun lateSidecarSurvivesRestartRenameAndStaleTranscriptFallsBack() {
        val dir = Files.createTempDirectory("vemo-speakers").toFile()
        try {
            val store = RecordingStore(dir)
            val item = store.create("Memo", Mode.DESKTOP, binding.processor)
            store.partial(item.id).writeText("audio")
            store.transition(item.id, CaptureState.FINALIZING)
            val record = store.finish(item.id, 4000)
            fun resultBytes(text: String) = Json.bytes(mapOf("schema_version" to 1, "recording_id" to record.id,
                "audio_sha256" to record.hash, "processor_id" to record.targetProcessor, "title" to "Memo",
                "recorded_at" to record.recordedAt, "transcript" to text, "notes_markdown" to "notes"))
            val original = resultBytes(transcript)
            store.saveResult(record, original)
            val cloud = MemoryCloud()
            val path = "results/${record.id}/${record.hash}/speakers.json"
            val engine = SyncEngine(cloud)
            assertNull(engine.speakers(record, store.result(record)!!))
            assertTrue(store.result(record)!!.needsSpeakerEnrichment)
            val sidecar = document(record.hash!!)
            cloud.files[path] = Json.bytes(sidecar)
            store.saveSpeakers(record, engine.speakers(record, store.result(record)!!)!!)
            assertEquals("민수", RecordingStore(dir).result(record)!!.speakers!![0].speakerName)
            assertFalse(store.result(record)!!.needsSpeakerEnrichment)
            sidecar.getAsJsonArray("speakers")[0].asJsonObject.addProperty("name", "새 이름")
            cloud.files[path] = Json.bytes(sidecar)
            store.saveSpeakers(record, engine.speakers(record, store.result(record)!!)!!)
            assertEquals("새 이름", store.result(record)!!.speakers!![0].speakerName)
            val invalid = document(record.hash).apply { addProperty("transcript_sha256", "b".repeat(64)) }
            assertThrows(ProtocolException::class.java) { store.saveSpeakers(record, Json.bytes(invalid)) }
            assertEquals("새 이름", store.result(record)!!.speakers!![0].speakerName)
            assertArrayEquals(original, java.io.File(store.audio(record.id).parentFile, "result.json").readBytes())
            java.io.File(store.audio(record.id).parentFile, "speakers.json").writeText("broken")
            assertNull(store.result(record)!!.speakers)
            assertEquals(transcript, store.result(record)!!.transcript)
            store.saveSpeakers(record, Json.bytes(sidecar))
            store.saveResult(record, resultBytes("changed"))
            assertNull(RecordingStore(dir).result(record)!!.speakers)
            assertEquals(record.hash, sha256(store.audio(record.id)))
        } finally { dir.deleteRecursively() }
    }
}
