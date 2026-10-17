package com.vemo.android.core

import com.google.gson.*
import com.google.gson.stream.JsonReader
import com.google.gson.stream.JsonToken
import java.io.*
import java.net.URI
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.nio.file.Files
import java.security.MessageDigest
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter

class ProtocolException(message: String) : IOException(message)
class ConflictException : IOException("Remote content conflict; original preserved")
class AuthException : IOException("Authentication failed; reconnect in Settings")
enum class Mode { RECORD_ONLY, DESKTOP }
enum class CaptureState { RECORDING, PAUSED, FINALIZING, COMPLETE, INTERRUPTED, FAILED }
data class Binding(val id: String, val hash: String, val processor: String)
data class Processor(val id: String, val displayName: String)
data class Result(val title: String, val recordedAt: String, val transcript: String, val notes: String)
data class Recording(
    val id: String, val title: String, val recordedAt: String, val mode: Mode,
    val targetProcessor: String?, val state: CaptureState, val hash: String? = null,
    val bytes: Long = 0, val durationMs: Long = 0, val status: String = "",
    val cloudKey: String? = null, val uploaded: Boolean = false, val hidden: Boolean = false,
    val exported: Boolean = false
) {
    val uploadable: Boolean get() = state == CaptureState.COMPLETE && hash != null && bytes > 0 && !hidden
}

/** Provider operations use paths relative to the user-selected root. Implementations must not overwrite. */
interface CloudStorage {
    fun ensureFolders(path: String)
    fun read(path: String, limit: Int): ByteArray?
    fun putImmutable(path: String, size: Long, mime: String, source: () -> InputStream)
}

fun safeRelativePath(path: String): List<String> {
    if (path.length > 2048 || path.isEmpty() || path.startsWith('/')) throw ProtocolException("Invalid relative folder")
    return path.split('/').also { parts ->
        if (parts.any { it.isBlank() || it == "." || it == ".." || it.length > 255 ||
                it.any { c -> c.isISOControl() || c in "\\%?#:" } }) throw ProtocolException("Invalid relative folder")
    }
}
fun readBounded(stream: InputStream, limit: Int): ByteArray {
    val out = ByteArrayOutputStream(minOf(limit, 8192))
    val buffer = ByteArray(8192)
    var total = 0
    while (true) {
        val count = stream.read(buffer, 0, minOf(buffer.size, limit - total + 1))
        if (count < 0) return out.toByteArray()
        total += count
        if (total > limit) throw ProtocolException("Remote JSON exceeds size limit")
        out.write(buffer, 0, count)
    }
}
fun digest(stream: InputStream, expectedSize: Long? = null): String {
    val md = MessageDigest.getInstance("SHA-256")
    var count = 0L
    val buffer = ByteArray(64 * 1024)
    while (true) {
        val n = stream.read(buffer)
        if (n < 0) break
        count += n
        if (expectedSize != null && count > expectedSize) throw ConflictException()
        md.update(buffer, 0, n)
    }
    if (expectedSize != null && count != expectedSize) throw ConflictException()
    return md.digest().joinToString("") { "%02x".format(it) }
}
fun sha256(file: File): String {
    if (!Files.isRegularFile(file.toPath(), java.nio.file.LinkOption.NOFOLLOW_LINKS)) throw ProtocolException("Audio must be a private regular file")
    return file.inputStream().use { digest(it) }
}
fun validUuid(value: String): String {
    if (!Regex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}").matches(value)) throw ProtocolException("Invalid UUID")
    return value
}
fun validHash(value: String): String {
    if (!Regex("[0-9a-f]{64}").matches(value)) throw ProtocolException("Invalid SHA256")
    return value
}
fun validDate(value: String): String {
    try {
        if (!Regex("[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,9})?(Z|[+-][0-9]{2}:[0-9]{2})").matches(value)) throw IllegalArgumentException()
        val parsed = OffsetDateTime.parse(value, DateTimeFormatter.ISO_OFFSET_DATE_TIME)
        if (parsed.year < 1 || kotlin.math.abs(parsed.offset.totalSeconds) > 14 * 60 * 60) throw IllegalArgumentException()
    } catch (_: Exception) { throw ProtocolException("Invalid recording date or timezone") }
    return value
}

/** Strict UTF-8/JSON, including duplicate key rejection, before any typed coercion. */
object Json {
    val gson: Gson = GsonBuilder().disableHtmlEscaping().serializeNulls().create()
    fun parse(bytes: ByteArray): JsonObject {
        if (bytes.size > Wire.MAX_JSON) throw ProtocolException("JSON exceeds 8 MiB")
        try {
            val decoder = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
            val reader = JsonReader(StringReader(decoder.decode(ByteBuffer.wrap(bytes)).toString())).apply { strictness = Strictness.STRICT }
            val parsed = read(reader, 0)
            if (reader.peek() != JsonToken.END_DOCUMENT || !parsed.isJsonObject) throw ProtocolException("Expected one JSON object")
            return parsed.asJsonObject
        } catch (e: ProtocolException) { throw e }
        catch (_: Exception) { throw ProtocolException("Invalid UTF-8 JSON") }
    }
    private fun read(r: JsonReader, depth: Int): JsonElement {
        if (depth > 24) throw ProtocolException("JSON nesting too deep")
        return when (r.peek()) {
            JsonToken.BEGIN_OBJECT -> JsonObject().also { obj ->
                r.beginObject()
                while (r.hasNext()) {
                    val name = r.nextName()
                    if (obj.has(name)) throw ProtocolException("Duplicate JSON field")
                    obj.add(name, read(r, depth + 1))
                }
                r.endObject()
            }
            JsonToken.BEGIN_ARRAY -> JsonArray().also { a -> r.beginArray(); while (r.hasNext()) a.add(read(r, depth + 1)); r.endArray() }
            JsonToken.STRING -> JsonPrimitive(r.nextString())
            JsonToken.NUMBER -> JsonPrimitive(java.math.BigDecimal(r.nextString()))
            JsonToken.BOOLEAN -> JsonPrimitive(r.nextBoolean())
            JsonToken.NULL -> { r.nextNull(); JsonNull.INSTANCE }
            else -> throw ProtocolException("Malformed JSON")
        }
    }
    fun string(obj: JsonObject, key: String): String {
        val value = obj[key]
        if (value == null || !value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw ProtocolException("Missing or invalid $key")
        return value.asString
    }
    fun long(obj: JsonObject, key: String): Long {
        try {
            val value = obj[key]
            if (value == null || !value.isJsonPrimitive || !value.asJsonPrimitive.isNumber) throw ProtocolException("Invalid $key")
            return value.asBigDecimal.longValueExact()
        } catch (_: ArithmeticException) { throw ProtocolException("Invalid $key") }
    }
    fun bytes(value: Any): ByteArray = gson.toJson(value).toByteArray(Charsets.UTF_8)
}
object Wire {
    const val MAX_JSON = 8 * 1024 * 1024
    private val processorKeys = setOf("schema_version", "processor_id", "display_name")
    private val resultKeys = setOf("schema_version", "recording_id", "audio_sha256", "processor_id", "title", "recorded_at", "transcript", "notes_markdown")
    fun processor(bytes: ByteArray): Processor {
        val obj = Json.parse(bytes)
        if (obj.keySet() != processorKeys) throw ProtocolException("Processor fields do not match v1 schema")
        if (Json.long(obj, "schema_version") != 1L) throw ProtocolException("Unsupported processor schema")
        val name = Json.string(obj, "display_name")
        if (name.isBlank() || name.length > 4096) throw ProtocolException("Invalid processor name")
        return Processor(validUuid(Json.string(obj, "processor_id")), name)
    }
    fun result(bytes: ByteArray, binding: Binding): Result {
        val obj = Json.parse(bytes)
        if (obj.keySet() != resultKeys) throw ProtocolException("Result fields do not match v1 schema")
        if (Json.long(obj, "schema_version") != 1L) throw ProtocolException("Unsupported result schema")
        val id = validUuid(Json.string(obj, "recording_id"))
        val hash = validHash(Json.string(obj, "audio_sha256"))
        val processor = validUuid(Json.string(obj, "processor_id"))
        if (id != binding.id || hash != binding.hash || processor != binding.processor) throw ProtocolException("Result identity does not match this recording")
        return Result(Json.string(obj, "title"), validDate(Json.string(obj, "recorded_at")), Json.string(obj, "transcript"), Json.string(obj, "notes_markdown"))
    }
    fun marker(record: Recording): ByteArray {
        if (!record.uploadable) throw ProtocolException("Only finalized recordings can upload")
        validUuid(record.id); validHash(record.hash!!); validDate(record.recordedAt)
        if (record.bytes > 16L * 1024 * 1024 * 1024) throw ProtocolException("Audio exceeds v1 16 GiB limit")
        if (record.mode == Mode.DESKTOP && record.targetProcessor == null) throw ProtocolException("Desktop processor has not been connected")
        record.targetProcessor?.let { validUuid(it) }
        return Json.bytes(linkedMapOf(
            "schema_version" to 1, "recording_id" to record.id, "audio_sha256" to record.hash,
            "audio_bytes" to record.bytes, "audio_file" to "audio.m4a", "recorded_at" to record.recordedAt,
            "title" to record.title, "mode" to if (record.mode == Mode.DESKTOP) "desktop" else "record_only",
            "target_processor_id" to record.targetProcessor, "completed" to true
        ))
    }
}
class SyncEngine(private val cloud: CloudStorage) {
    fun processor(): Processor = Wire.processor(cloud.read("processor.json", Wire.MAX_JSON) ?: throw ProtocolException("No processor.json in selected folder. Register the desktop first."))
    fun upload(record: Recording, file: File) {
        val marker = Wire.marker(record)
        if (file.length() != record.bytes || sha256(file) != record.hash) throw ProtocolException("Local audio no longer matches finalized hash")
        val directory = "recordings/${record.id}/${record.hash}"
        cloud.ensureFolders(directory)
        cloud.putImmutable("$directory/audio.m4a", record.bytes, "audio/mp4") { file.inputStream() }
        cloud.putImmutable("$directory/recording.json", marker.size.toLong(), "application/json; charset=utf-8") { ByteArrayInputStream(marker) }
    }
    fun result(record: Recording): Pair<Result, ByteArray>? {
        if (!record.uploadable || record.mode != Mode.DESKTOP || record.targetProcessor == null) return null
        val bytes = cloud.read("results/${record.id}/${record.hash}/result.json", Wire.MAX_JSON) ?: return null
        val result = Wire.result(bytes, Binding(record.id, record.hash!!, record.targetProcessor))
        if (result.recordedAt != record.recordedAt) throw ProtocolException("Result changed original recording date")
        return result to bytes
    }
}
data class DavConfig(val endpoint: String, val folder: String) {
    fun validate() {
        try {
            val uri = URI(endpoint)
            if (uri.scheme != "https" || uri.host.isNullOrBlank() || uri.userInfo != null || uri.query != null || uri.fragment != null || uri.port == 0 || uri.port < -1) throw ProtocolException("Use an HTTPS WebDAV URL without embedded credentials, query or fragment")
            if (uri.rawPath.orEmpty().contains('%') || uri.path.orEmpty().contains('\\') || uri.path.orEmpty().split('/').any { it == "." || it == ".." || it.any(Char::isISOControl) }) throw ProtocolException("Invalid WebDAV endpoint path")
            safeRelativePath(folder)
        } catch (e: ProtocolException) { throw e }
        catch (_: Exception) { throw ProtocolException("Invalid HTTPS WebDAV URL") }
    }
}
class OperationGate {
    private var recording = false
    private var syncing = false
    @Synchronized fun beginRecording(): Boolean { if (recording || syncing) return false; recording = true; return true }
    @Synchronized fun endRecording() { recording = false }
    @Synchronized fun beginSync(): Boolean { if (recording || syncing) return false; syncing = true; return true }
    @Synchronized fun endSync() { syncing = false }
    @Synchronized fun busy(): Boolean = recording || syncing
    @Synchronized fun isRecording(): Boolean = recording
}
