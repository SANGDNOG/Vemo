package com.vemo.android.core

import com.google.gson.JsonObject
import okhttp3.HttpUrl.Companion.toHttpUrl
import java.io.ByteArrayInputStream
import java.io.InputStream

const val DRIVE_FILE_SCOPE = "https://www.googleapis.com/auth/drive.file"
private const val FOLDER_MIME = "application/vnd.google-apps.folder"
private const val FILE_FIELDS = "id,name,mimeType,parents,properties,trashed,size"

data class DriveBinding(val accountId: String, val rootId: String, val processorId: String) {
    init { driveId(accountId); driveId(rootId); validUuid(processorId) }
    fun identity(): String = "drive:" + digest(ByteArrayInputStream(Json.bytes(listOf(accountId, rootId, processorId))))
}
data class DriveRoot(val id: String, val name: String, val processorId: String)
/** The reservation must reach durable storage BEFORE any create request is sent. */
interface DriveReservations {
    fun get(key: String): String?
    fun save(key: String, id: String)
}
private fun driveId(id: String): String {
    if (!Regex("[A-Za-z0-9_-]{1,256}").matches(id)) throw ProtocolException("Invalid Drive identity")
    return id
}
fun drivePathKey(path: String): String {
    safeRelativePath(path)
    return digest(ByteArrayInputStream(path.toByteArray(Charsets.UTF_8)))
}

/** Fixed API origin; no redirects, automatic retries, token persistence or name-only lookup. */
class GoogleDriveApi(private val token: () -> String, private val transport: HttpTransport = SecureHttpTransport(),
    private val cancelled: () -> Boolean = { false }) {
    internal fun call(method: String, path: String, query: Map<String, String> = emptyMap(), size: Long = 0,
        mime: String = "application/json", body: (() -> InputStream)? = null,
        headers: Map<String, String> = emptyMap(), sessionLocation: Boolean = false): HttpReply {
        if (Thread.currentThread().isInterrupted || cancelled()) throw java.io.InterruptedIOException()
        val builder = "https://www.googleapis.com".toHttpUrl().newBuilder().addPathSegments(path)
        query.forEach { (k, v) -> builder.addQueryParameter(k, v) }
        return execute(HttpRequest(method, builder.build().toUri(), headers, size, mime, body), sessionLocation)
    }
    private fun execute(request: HttpRequest, sessionLocation: Boolean = false): HttpReply {
        if (Thread.currentThread().isInterrupted || cancelled()) throw java.io.InterruptedIOException()
        val access = token()
        if (access.isBlank() || access.any(Char::isISOControl)) throw AuthException()
        if (cancelled()) throw java.io.InterruptedIOException()
        val reply = transport.execute(request.copy(headers = request.headers + mapOf("Authorization" to "Bearer $access", "Accept-Encoding" to "identity")))
        if (reply.code == 401 || reply.code == 403) { reply.close(); throw AuthException() }
        if (reply.code in 300..399 || (!sessionLocation && reply.header("Location") != null) || reply.header("Content-Location") != null) { reply.close(); throw ProtocolException("Drive redirect rejected") }
        if (Thread.currentThread().isInterrupted || cancelled()) {
            reply.close()
            throw java.io.InterruptedIOException("Drive operation cancelled")
        }
        return reply.copy(body = cancellationChecked(reply.body, cancelled))
    }
    internal fun upload(metadata: ByteArray, size: Long, mime: String, source: () -> InputStream) {
        val location = call("POST", "upload/drive/v3/files", mapOf("uploadType" to "resumable"), metadata.size.toLong(),
            body = { ByteArrayInputStream(metadata) }, headers = mapOf("X-Upload-Content-Type" to mime, "X-Upload-Content-Length" to size.toString()), sessionLocation = true).use {
            if (it.code == 409) return // Generated ID already exists: inspect and hash below.
            if (it.code != 200) throw ProtocolException("Drive upload initiation failed (HTTP ${it.code})")
            it.header("Location") ?: throw ProtocolException("Missing Drive upload session")
        }
        // A resumable Location is the sole allowed reference, checked before sending credentials or audio.
        val url = try { location.toHttpUrl() } catch (_: Exception) { throw ProtocolException("Invalid Drive upload session") }
        // Google may add an opaque session credential. Preserve it only in its bounded, single-value form.
        val sessionCredentials = url.queryParameterValues("session_crd")
        val expectedKeys = if (sessionCredentials.isEmpty()) setOf("uploadType", "upload_id")
            else setOf("uploadType", "upload_id", "session_crd")
        if (location.length > 8192 || url.scheme != "https" || url.host != "www.googleapis.com" || url.port != 443 ||
            url.username.isNotEmpty() || url.password.isNotEmpty() || url.fragment != null || url.encodedPath != "/upload/drive/v3/files" ||
            url.queryParameterNames != expectedKeys ||
            (sessionCredentials.isNotEmpty() && (sessionCredentials.size != 1 || !Regex("[A-Za-z0-9_-]{1,2048}").matches(sessionCredentials.single().orEmpty()))) ||
            url.queryParameterValues("uploadType") != listOf("resumable") ||
            url.queryParameterValues("upload_id").size != 1 || !Regex("[A-Za-z0-9_-]{1,2048}").matches(url.queryParameter("upload_id").orEmpty())) throw ProtocolException("Untrusted Drive upload session")
        execute(HttpRequest("PUT", url.toUri(), size = size, mime = mime, body = source)).use {
            if (it.code !in listOf(200, 201, 409)) throw ProtocolException("Drive upload incomplete (HTTP ${it.code}); retry with same identity")
        }
    }
    internal fun json(path: String, query: Map<String, String> = emptyMap()): JsonObject = call("GET", path, query).use {
        if (it.code != 200) throw ProtocolException("Drive inspection failed (HTTP ${it.code})")
        Json.parse(readBounded(it.body, Wire.MAX_JSON))
    }
    fun accountId(): String = driveId(Json.string(json("drive/v3/about", mapOf("fields" to "user(permissionId)" )).getAsJsonObject("user"), "permissionId"))
    internal fun file(id: String): JsonObject? = call("GET", "drive/v3/files/${driveId(id)}", mapOf("fields" to FILE_FIELDS)).use {
        if (it.code == 404) return null
        if (it.code != 200) throw ProtocolException("Drive file inspection failed (HTTP ${it.code})")
        Json.parse(readBounded(it.body, Wire.MAX_JSON)).also { file ->
            if (Json.string(file, "id") != id) throw ProtocolException("Drive returned a different file identity")
        }
    }
    internal fun list(query: String): List<JsonObject> {
        val files = mutableListOf<JsonObject>()
        val seen = mutableSetOf<String>()
        var page: String? = null
        repeat(100) {
            val params = mutableMapOf("q" to query, "spaces" to "drive", "pageSize" to "100", "fields" to "nextPageToken,incompleteSearch,files($FILE_FIELDS)")
            page?.let { params["pageToken"] = it }
            val obj = json("drive/v3/files", params)
            if (obj["incompleteSearch"]?.asBoolean == true) throw ProtocolException("Incomplete Drive search")
            val batch = obj.getAsJsonArray("files") ?: throw ProtocolException("Missing Drive listing")
            batch.forEach { item ->
                val file = item.asJsonObject
                if (!seen.add(Json.string(file, "id"))) throw ProtocolException("Repeated Drive search identity")
                files.add(file)
            }
            page = obj["nextPageToken"]?.let { Json.string(obj, "nextPageToken") }
            if (page == null) return files
            if (page!!.isBlank()) throw ProtocolException("Invalid Drive pagination")
        }
        throw ProtocolException("Drive pagination limit exceeded")
    }
    fun discoverRoots(): List<DriveRoot> {
        val roots = list("trashed = false and properties has { key='vemoKind' and value='root' }").map { root ->
            val processor = rootProcessor(root)
            DriveRoot(driveId(Json.string(root, "id")), Json.string(root, "name"), processor)
        }
        if (roots.groupBy { it.name }.any { it.value.size > 1 } || roots.groupBy { it.processorId }.any { it.value.size > 1 }) throw ProtocolException("Duplicate Drive roots/processors; resolve on desktop")
        return roots
    }
    internal fun validate(binding: DriveBinding) {
        if (accountId() != binding.accountId) throw ProtocolException("Google account changed; reconnect original account")
        val root = file(binding.rootId) ?: throw ProtocolException("Selected Drive root unavailable")
        if (rootProcessor(root) != binding.processorId) throw ProtocolException("Drive processor changed; original binding preserved")
    }
    private fun rootProcessor(root: JsonObject): String {
        if (root["trashed"]?.asBoolean != false || Json.string(root, "mimeType") != FOLDER_MIME) throw ProtocolException("Invalid Drive root MIME/state")
        val props = root.getAsJsonObject("properties") ?: throw ProtocolException("Missing Drive root metadata")
        if (Json.string(props, "vemoProtocol") != "1" || Json.string(props, "vemoKind") != "root") throw ProtocolException("Invalid Drive root metadata")
        return validUuid(Json.string(props, "vemoProcessor"))
    }
}

class GoogleDriveCloud(private val api: GoogleDriveApi, val binding: DriveBinding,
    private val reservations: DriveReservations) : CloudStorage {
    init { api.validate(binding) }
    private fun validate(file: JsonObject, path: String, parent: String, mime: String): String {
        val props = file.getAsJsonObject("properties") ?: throw ConflictException()
        val parents = file.getAsJsonArray("parents") ?: throw ConflictException()
        if (file["trashed"]?.asBoolean != false || Json.string(file, "name") != path.substringAfterLast('/') ||
            Json.string(file, "mimeType") != mime || parents.size() != 1 || parents[0].asString != parent ||
            Json.string(props, "vemoProtocol") != "1" || Json.string(props, "vemoKey") != drivePathKey(path)) throw ConflictException()
        return driveId(Json.string(file, "id"))
    }
    private fun lookup(path: String, parent: String, mime: String): JsonObject? {
        val name = path.substringAfterLast('/').replace("'", "\\'")
        // Include same-name and same-key candidates so malformed or duplicated objects cannot be ignored.
        val candidates = api.list("trashed = false and '$parent' in parents and (name = '$name' or properties has { key='vemoKey' and value='${drivePathKey(path)}' })")
        if (candidates.size > 1) throw ConflictException()
        return candidates.singleOrNull()?.also {
            val id = validate(it, path, parent, mime)
            reservations.get(binding.identity() + ":" + drivePathKey(path))?.let { reserved ->
                if (id != reserved) throw ConflictException()
            }
        }
    }
    private fun reserved(path: String): String {
        val key = binding.identity() + ":" + drivePathKey(path)
        reservations.get(key)?.let { return driveId(it) }
        val ids = api.json("drive/v3/files/generateIds", mapOf("count" to "1", "space" to "drive", "type" to "files")).getAsJsonArray("ids")
        if (ids == null || ids.size() != 1) throw ProtocolException("Drive ID reservation failed")
        val id = driveId(ids[0].asString)
        reservations.save(key, id)
        return id
    }
    private fun metadata(path: String, parent: String, mime: String, id: String) = Json.bytes(mapOf(
        "id" to id, "name" to path.substringAfterLast('/'), "parents" to listOf(parent), "mimeType" to mime,
        "properties" to mapOf("vemoProtocol" to "1", "vemoKey" to drivePathKey(path))))
    private fun create(path: String, parent: String, mime: String, size: Long = 0, source: (() -> InputStream)? = null): JsonObject {
        val id = reserved(path)
        // A lost acknowledgement is resolved by the persisted ID, even if Drive search is delayed.
        api.file(id)?.let { validate(it, path, parent, mime); return it }
        val meta = metadata(path, parent, mime, id)
        if (source == null) {
            api.call("POST", "drive/v3/files", size = meta.size.toLong(), body = { ByteArrayInputStream(meta) }).use {
                if (it.code !in listOf(200, 201, 409)) throw ProtocolException("Drive folder create failed (HTTP ${it.code}); retry with same identity")
            }
        } else api.upload(meta, size, mime, source)
        val created = api.file(id) ?: throw ProtocolException("Drive create not confirmed; retry")
        validate(created, path, parent, mime)
        val listed = lookup(path, parent, mime)
        if (listed != null && Json.string(listed, "id") != id) throw ConflictException()
        return created
    }
    private fun parent(path: String, create: Boolean): String? {
        val parts = safeRelativePath(path)
        var parent = binding.rootId
        for (i in 1 until parts.size) {
            val folder = parts.take(i).joinToString("/")
            val file = lookup(folder, parent, FOLDER_MIME) ?: if (create) create(folder, parent, FOLDER_MIME) else return null
            parent = Json.string(file, "id")
        }
        return parent
    }
    override fun ensureFolders(path: String) { api.validate(binding); parent("$path/placeholder", true) }
    override fun read(path: String, limit: Int): ByteArray? {
        require(limit in 1..Wire.MAX_JSON)
        api.validate(binding)
        val parent = parent(path, false) ?: return null
        val file = lookup(path, parent, "application/json") ?: return null
        val id = Json.string(file, "id")
        return api.call("GET", "drive/v3/files/$id", mapOf("alt" to "media")).use {
            if (it.code != 200) throw ProtocolException("Drive download failed (HTTP ${it.code})")
            val bytes = readBounded(it.body, limit)
            if (Json.string(file, "size").toLongOrNull() != bytes.size.toLong()) throw ProtocolException("Truncated Drive JSON")
            bytes
        }
    }
    override fun putImmutable(path: String, size: Long, mime: String, source: () -> InputStream) {
        if (size <= 0 || size > 16L * 1024 * 1024 * 1024) throw ProtocolException("Drive file size limit")
        val type = mime.substringBefore(';').trim()
        if (type !in setOf("audio/mp4", "application/json") || (type == "application/json" && size > Wire.MAX_JSON)) throw ProtocolException("Unsupported Drive content")
        api.validate(binding)
        val parent = parent(path, false) ?: throw ProtocolException("Drive parent missing")
        val expected = source().use { digest(it, size) }
        val file = lookup(path, parent, type) ?: create(path, parent, type, size, source)
        validate(file, path, parent, type)
        if (Json.string(file, "size").toLongOrNull() != size) throw ConflictException()
        api.call("GET", "drive/v3/files/${Json.string(file, "id")}", mapOf("alt" to "media")).use {
            if (it.code != 200) throw ProtocolException("Drive upload confirmation failed")
            if (digest(it.body, size) != expected) throw ConflictException()
        }
    }
}
