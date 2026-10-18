package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream

class TranscriptTimelineTest {
    private val binding = Binding("01234567-89ab-4cde-8fab-0123456789ab", "a".repeat(64), "11234567-89ab-4cde-8fab-0123456789ab")
    private val transcript = "첫 문장입니다. 다음 문장입니다!"
    private fun sidecar(segments: List<Map<String, Any?>> = listOf(
        mapOf("start" to 1.25, "end" to 4.0, "text" to "첫 문장입니다. "),
        mapOf("start" to null, "end" to null, "text" to "다음 문장입니다!")
    )) = Json.bytes(mapOf("schema_version" to 1, "recording_id" to binding.id,
        "audio_sha256" to binding.hash, "processor_id" to binding.processor,
        "transcript_sha256" to digest(ByteArrayInputStream(transcript.toByteArray(Charsets.UTF_8))), "segments" to segments))
    private fun parse(bytes: ByteArray) = TranscriptTimeline.parse(bytes, binding, transcript, 10_000)

    @Test fun preservesActualTimingAndUntimedRows() {
        val rows = parse(sidecar())
        assertEquals(listOf("첫 문장입니다. ", "다음 문장입니다!"), rows.map { it.text })
        assertEquals(1250L, rows.first().startMs)
        assertEquals("00:01", rows.first().timestamp)
        assertNull(rows.last().startMs)
        assertNull(rows.last().timestamp)
    }
    @Test fun rejectsStaleIdentityHashAndMissingText() {
        for ((key, value) in listOf("recording_id" to binding.processor, "audio_sha256" to "b".repeat(64),
            "processor_id" to binding.id, "transcript_sha256" to "c".repeat(64))) {
            val obj = Json.parse(sidecar()).apply { addProperty(key, value) }
            assertThrows(ProtocolException::class.java) { parse(Json.bytes(obj)) }
        }
        assertThrows(ProtocolException::class.java) { parse(sidecar(listOf(mapOf("start" to 1, "end" to 2, "text" to "첫 문장입니다.")))) }
        assertThrows(ProtocolException::class.java) { parse(sidecar(listOf(mapOf("start" to 1, "end" to 2, "text" to transcript.replace("!", "?"))))) }
    }
    @Test fun rejectsInvalidBoundsAndHalfNullTiming() {
        for ((start, end) in listOf(-1 to 2, 3 to 2, 1 to 11, null to 2, 1 to null, "1" to 2)) {
            assertThrows(ProtocolException::class.java) { parse(sidecar(listOf(mapOf("start" to start, "end" to end, "text" to transcript)))) }
        }
    }
    @Test fun acceptsOverlappingAlignmentButRejectsBackwardTimes() {
        fun segments(start: Double, end: Double) = listOf(
            mapOf("start" to 1.0, "end" to 4.0, "text" to "첫 문장입니다. "),
            mapOf("start" to start, "end" to end, "text" to "다음 문장입니다!"))
        assertEquals(2, parse(sidecar(segments(1.0, 4.0))).size)
        for ((start, end) in listOf(0.5 to 5.0, 2.0 to 3.0)) {
            assertThrows(ProtocolException::class.java) { parse(sidecar(segments(start, end))) }
        }
    }
    @Test fun rejectsMalformedOptionalDocumentsAndNonfiniteTimes() {
        for (bytes in listOf("{}".toByteArray(), "null".toByteArray(), byteArrayOf(0xc3.toByte()),
            sidecar().toString(Charsets.UTF_8).replace("1.25", "1e9999").toByteArray(),
            sidecar().toString(Charsets.UTF_8).replace("1.25", "null").toByteArray(),
            sidecar().toString(Charsets.UTF_8).replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1").toByteArray())) {
            assertThrows(ProtocolException::class.java) { parse(bytes) }
        }
    }
    @Test fun identicalZeroLengthSpansAreAcceptedAndHoursDoNotWrap() {
        val rows = parse(sidecar(listOf(mapOf("start" to 0, "end" to 0, "text" to transcript))))
        assertEquals("00:00", rows.single().timestamp)
        assertEquals("1:01:01", TranscriptRow("text", 3_661_999).timestamp)
    }
    @Test fun absentEmptyAndAllNullTimelinesKeepEnrichmentEligible() {
        val legacy = Result("title", "2026-10-18T00:00:00Z", "sentence", "notes")
        assertTrue(legacy.needsTimelineEnrichment)
        assertTrue(legacy.copy(timeline = emptyList()).needsTimelineEnrichment)
        assertTrue(legacy.copy(timeline = listOf(TranscriptRow("sentence"))).needsTimelineEnrichment)
        assertFalse(legacy.copy(timeline = listOf(TranscriptRow("sentence", 0))).needsTimelineEnrichment)
        assertFalse(legacy.copy(timeline = listOf(TranscriptRow("first"), TranscriptRow("second", 1250))).needsTimelineEnrichment)
    }
    @Test fun legacySentencesPreserveTextWithoutInventedTimes() {
        val source = "첫 문장입니다. 다음 문장입니다!\n마지막 줄입니다"
        val rows = TranscriptTimeline.plain(source)
        assertEquals(listOf("첫 문장입니다. ", "다음 문장입니다!\n", "마지막 줄입니다"), rows.map { it.text })
        assertEquals(source, rows.joinToString("") { it.text })
        assertTrue(rows.all { it.startMs == null && it.timestamp == null })
    }
    @Test fun longUnpunctuatedTextRemainsReadableAndLossless() {
        val source = "가나다 ".repeat(250)
        val rows = TranscriptTimeline.plain(source)
        assertEquals(source, rows.joinToString("") { it.text })
        assertTrue(rows.all { it.text.codePointCount(0, it.text.length) <= 320 })
    }
}
