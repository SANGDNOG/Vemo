package com.vemo.android.core

import com.google.gson.JsonObject
import java.io.ByteArrayInputStream
import java.text.Normalizer
import java.text.BreakIterator
import java.util.Locale

/** Optional desktop-authored presentation metadata; never part of the v1 result. */
object TranscriptSpeakers {
    const val FILE_NAME = "speakers.json"
    private const val MAX_ITEMS = 100000
    private val keys = setOf("schema_version", "audio_sha256", "transcript_sha256", "generation", "speakers", "turns", "segments")
    private data class Interval(val start: Double, val end: Double, val id: String?)
    private data class Segment(val interval: Interval, val text: String)

    fun parse(bytes: ByteArray, audioHash: String, transcript: String, durationMs: Long): List<TranscriptRow> {
        val obj = Json.parse(bytes)
        if (obj.keySet() != keys || Json.long(obj, "schema_version") != 1L)
            throw ProtocolException("Invalid speaker schema")
        if (validHash(Json.string(obj, "audio_sha256")) != validHash(audioHash) ||
            validHash(Json.string(obj, "transcript_sha256")) != digest(ByteArrayInputStream(transcript.toByteArray(Charsets.UTF_8))))
            throw ProtocolException("Speaker identity mismatch")
        validUuid(Json.string(obj, "generation"))
        fun collection(key: String): List<JsonObject> {
            val value = obj[key]
            if (value == null || !value.isJsonArray || value.asJsonArray.size() > MAX_ITEMS)
                throw ProtocolException("Invalid speaker $key")
            return value.asJsonArray.map {
                if (!it.isJsonObject) throw ProtocolException("Invalid speaker entry")
                it.asJsonObject
            }
        }
        val names = mutableMapOf<String, String>()
        for (speaker in collection("speakers")) {
            if (speaker.keySet() != setOf("id", "name")) throw ProtocolException("Invalid speaker fields")
            val id = Json.string(speaker, "id")
            val name = Json.string(speaker, "name")
            if (!Regex("speaker_[1-9][0-9]*").matches(id) || names.containsKey(id))
                throw ProtocolException("Invalid or duplicate speaker ID")
            if (name != name.trim { Character.isWhitespace(it) || Character.isSpaceChar(it) } ||
                name.codePointCount(0, name.length) !in 1..80 || name.codePoints().anyMatch { forbiddenNameCodePoint(it) })
                throw ProtocolException("Invalid speaker name")
            names[id] = name
        }
        fun reference(item: JsonObject, nullable: Boolean): String? {
            val value = item["speaker_id"] ?: throw ProtocolException("Missing speaker reference")
            if (nullable && value.isJsonNull) return null
            val id = Json.string(item, "speaker_id")
            if (id !in names) throw ProtocolException("Unknown speaker reference")
            return id
        }
        fun timing(item: JsonObject, id: String?): Interval {
            fun seconds(key: String): Double {
                val value = item[key]
                if (value == null || !value.isJsonPrimitive || !value.asJsonPrimitive.isNumber)
                    throw ProtocolException("Invalid speaker time")
                return value.asDouble.also { if (!it.isFinite()) throw ProtocolException("Nonfinite speaker time") }
            }
            val start = seconds("start_s")
            val end = seconds("end_s")
            if (durationMs <= 0 || start < 0 || end < start || end > minOf(3600.0, durationMs / 1000.0))
                throw ProtocolException("Speaker time outside recording")
            return Interval(start, end, id)
        }
        val turns = collection("turns").map { turn ->
            if (turn.keySet() != setOf("start_s", "end_s", "speaker_id")) throw ProtocolException("Invalid speaker turn fields")
            timing(turn, reference(turn, false))
        }
        var previousStart = 0.0
        var previousEnd = 0.0
        val segments = collection("segments").map { segment ->
            if (segment.keySet() != setOf("start_s", "end_s", "text", "speaker_id"))
                throw ProtocolException("Invalid speaker segment fields")
            val interval = timing(segment, reference(segment, true))
            if (interval.start < previousStart || interval.end < previousEnd)
                throw ProtocolException("Unsorted speaker segments")
            previousStart = interval.start; previousEnd = interval.end
            Segment(interval, Json.string(segment, "text"))
        }
        if (normalized(segments.joinToString("") { it.text }) != normalized(transcript))
            throw ProtocolException("Speaker transcript coverage mismatch")
        validateAssignments(segments, turns)
        return displayRows(segments, names, transcript)
    }

    /** Sweep turns into disjoint spans, unioning duplicate coverage by the same speaker. */
    private fun validateAssignments(segments: List<Segment>, turns: List<Interval>) {
        data class Event(val time: Double, val id: String, val delta: Int)
        val events = turns.filter { it.end > it.start }.flatMap {
            val id = it.id!!
            listOf(Event(it.start, id, 1), Event(it.end, id, -1))
        }.sortedBy { it.time }
        val active = mutableMapOf<String, Int>()
        val spans = mutableListOf<Interval>()
        var previous = 0.0
        var index = 0
        while (index < events.size) {
            val point = events[index].time
            if (point > previous && active.isNotEmpty())
                spans.add(Interval(previous, point, if (active.size == 1) active.keys.first() else null))
            while (index < events.size && events[index].time == point) {
                val event = events[index++]
                val count = active.getOrDefault(event.id, 0) + event.delta
                if (count == 0) active.remove(event.id) else active[event.id] = count
            }
            previous = point
        }
        var operations = 0
        for (segment in segments) {
            val interval = segment.interval
            // A null assignment is always conservative; never manufacture an assignment on Android.
            val id = interval.id ?: continue
            var low = 0
            var high = spans.size
            while (low < high) {
                val middle = (low + high) ushr 1
                if (spans[middle].end <= interval.start) low = middle + 1 else high = middle
            }
            var covered = 0.0
            while (low < spans.size && spans[low].start < interval.end) {
                if (++operations > 2_000_000) throw ProtocolException("Speaker assignment complexity limit")
                val span = spans[low++]
                val overlap = minOf(interval.end, span.end) - maxOf(interval.start, span.start)
                if (overlap > 0) {
                    if (span.id != id) throw ProtocolException("Ambiguous speaker assignment")
                    covered += overlap
                }
            }
            if (covered <= (interval.end - interval.start) / 2)
                throw ProtocolException("Speaker assignment lacks majority coverage")
        }
    }

    /** Project normalized word ownership back onto original UTF-16 offsets, retaining original text. */
    private fun displayRows(segments: List<Segment>, names: Map<String, String>, transcript: String): List<TranscriptRow> {
        if (segments.isEmpty()) return TranscriptTimeline.plain(transcript)
        val normalizedStarts = mutableListOf<Int>()
        val characters = BreakIterator.getCharacterInstance(Locale.ROOT).apply { setText(transcript) }
        var start = characters.first()
        var end = characters.next()
        while (end != BreakIterator.DONE) {
            val normalized = normalized(transcript.substring(start, end))
            repeat(normalized.codePointCount(0, normalized.length)) { normalizedStarts.add(start) }
            start = end; end = characters.next()
        }
        val starts = IntArray(segments.size)
        var count = 0
        for ((index, segment) in segments.withIndex()) {
            starts[index] = if (index == 0) 0 else normalizedStarts.getOrElse(count) { transcript.length }
            val text = normalized(segment.text)
            count += text.codePointCount(0, text.length)
        }
        // A cluster cannot be split without fabricating a boundary (e.g. decomposed Hangul).
        if (count != normalizedStarts.size) throw ProtocolException("Speaker text projection mismatch")
        val rows = mutableListOf<TranscriptRow>()
        var first = 0
        while (first < segments.size) {
            var after = first + 1
            while (after < segments.size && segments[after].interval.id == segments[first].interval.id) after++
            val limit = if (after == segments.size) transcript.length else starts[after]
            var offset = starts[first]
            var owner = first
            for (row in TranscriptTimeline.plain(transcript.substring(offset, limit))) {
                while (owner + 1 < after && starts[owner + 1] <= offset) owner++
                val interval = segments[owner].interval
                rows.add(TranscriptRow(row.text, (interval.start * 1000).toLong(), interval.id?.let { names.getValue(it) }))
                offset += row.text.length
            }
            first = after
        }
        return rows
    }

    private fun forbiddenNameCodePoint(cp: Int): Boolean = when (Character.getType(cp)) {
        Character.CONTROL.toInt(), Character.FORMAT.toInt(), Character.SURROGATE.toInt(),
        Character.PRIVATE_USE.toInt(), Character.UNASSIGNED.toInt() -> true
        else -> false
    }

    /** Matches desktop TranscriptDisplay: NFC, excluding whitespace and Unicode punctuation. */
    private fun normalized(text: String): String = buildString {
        Normalizer.normalize(text, Normalizer.Form.NFC).codePoints().forEach { cp ->
            val punctuation = when (Character.getType(cp)) {
                Character.CONNECTOR_PUNCTUATION.toInt(),
                Character.DASH_PUNCTUATION.toInt(), Character.START_PUNCTUATION.toInt(), Character.END_PUNCTUATION.toInt(),
                Character.INITIAL_QUOTE_PUNCTUATION.toInt(), Character.FINAL_QUOTE_PUNCTUATION.toInt(), Character.OTHER_PUNCTUATION.toInt() -> true
                else -> false
            }
            if (!punctuation && !Character.isWhitespace(cp) && !Character.isSpaceChar(cp) && cp != 0x85) appendCodePoint(cp)
        }
    }
}
