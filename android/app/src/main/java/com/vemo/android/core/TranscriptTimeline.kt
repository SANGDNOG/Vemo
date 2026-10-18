package com.vemo.android.core

import java.io.ByteArrayInputStream
import java.text.BreakIterator
import java.util.Locale

/** Presentation only: raw v1 result text and exports remain untouched. */
data class TranscriptRow(val text: String, val startMs: Long? = null) {
    val timestamp: String? get() = startMs?.let {
        val seconds = it / 1000
        if (seconds >= 3600) String.format(Locale.ROOT, "%d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60)
        else String.format(Locale.ROOT, "%02d:%02d", seconds / 60, seconds % 60)
    }
}

object TranscriptTimeline {
    const val FILE_NAME = "transcript-timeline.json"
    private val keys = setOf("schema_version", "recording_id", "audio_sha256", "processor_id", "transcript_sha256", "segments")

    fun parse(bytes: ByteArray, binding: Binding, transcript: String, durationMs: Long): List<TranscriptRow> {
        val obj = Json.parse(bytes)
        if (obj.keySet() != keys || Json.long(obj, "schema_version") != 1L) throw ProtocolException("Invalid timeline schema")
        if (validUuid(Json.string(obj, "recording_id")) != binding.id ||
            validHash(Json.string(obj, "audio_sha256")) != binding.hash ||
            validUuid(Json.string(obj, "processor_id")) != binding.processor) throw ProtocolException("Timeline identity mismatch")
        val transcriptHash = digest(ByteArrayInputStream(transcript.toByteArray(Charsets.UTF_8)))
        if (validHash(Json.string(obj, "transcript_sha256")) != transcriptHash) throw ProtocolException("Timeline transcript mismatch")
        val segments = obj["segments"]
        if (segments == null || !segments.isJsonArray) throw ProtocolException("Invalid timeline segments")
        var previousStart = 0.0
        var previousEnd = 0.0
        val rows = segments.asJsonArray.map { element ->
            if (!element.isJsonObject) throw ProtocolException("Invalid timeline segment")
            val segment = element.asJsonObject
            if (segment.keySet() != setOf("start", "end", "text")) throw ProtocolException("Invalid timeline segment fields")
            val text = Json.string(segment, "text")
            if (text.isEmpty()) throw ProtocolException("Empty timeline row")
            if (segment["start"].isJsonNull && segment["end"].isJsonNull) TranscriptRow(text)
            else {
                fun seconds(key: String): Double {
                    val value = segment[key]
                    if (!value.isJsonPrimitive || !value.asJsonPrimitive.isNumber) throw ProtocolException("Invalid timeline time")
                    return value.asDouble.also { if (!it.isFinite()) throw ProtocolException("Nonfinite timeline time") }
                }
                val start = seconds("start")
                val end = seconds("end")
                if (start < previousStart || end < previousEnd || end < start || durationMs <= 0 || end > durationMs / 1000.0)
                    throw ProtocolException("Timeline time outside recording or out of order")
                previousStart = start; previousEnd = end
                TranscriptRow(text, (start * 1000).toLong())
            }
        }
        if (rows.joinToString("") { it.text } != transcript) throw ProtocolException("Timeline text coverage mismatch")
        return rows
    }

    fun plain(transcript: String): List<TranscriptRow> {
        if (transcript.isEmpty()) return emptyList()
        val boundaries = sortedSetOf(transcript.length)
        val sentences = BreakIterator.getSentenceInstance(Locale.KOREAN).apply { setText(transcript) }
        var boundary = sentences.first()
        while (boundary != BreakIterator.DONE) {
            if (boundary > 0) boundaries.add(boundary)
            boundary = sentences.next()
        }
        transcript.forEachIndexed { index, c -> if (c == '\n' || c == '\r') boundaries.add(index + 1) }
        val rows = mutableListOf<TranscriptRow>()
        var start = 0
        for (end in boundaries) {
            while (start < end) {
                val count = transcript.codePointCount(start, end)
                var next = if (count <= 320) end else transcript.offsetByCodePoints(start, 320)
                if (next < end) {
                    val space = (next - 1 downTo start).firstOrNull { transcript[it].isWhitespace() }
                    if (space != null) next = space + 1
                }
                rows.add(TranscriptRow(transcript.substring(start, next)))
                start = next
            }
        }
        return rows
    }
}
