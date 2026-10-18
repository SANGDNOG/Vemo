package com.vemo.android.core

import java.io.*
import java.net.URI
import java.util.Base64
import javax.xml.parsers.DocumentBuilderFactory
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.HttpUrl.Companion.toHttpUrl
import okio.BufferedSink

/** No redirect following, cookies, retry, logging, or non-default trust manager. */
class SecureHttpTransport internal constructor(private val client: OkHttpClient) : HttpTransport {
    constructor() : this(OkHttpClient.Builder().followRedirects(false).followSslRedirects(false)
        .retryOnConnectionFailure(false).connectTimeout(20, java.util.concurrent.TimeUnit.SECONDS)
        .readTimeout(60, java.util.concurrent.TimeUnit.SECONDS).writeTimeout(120, java.util.concurrent.TimeUnit.SECONDS)
        .callTimeout(20, java.util.concurrent.TimeUnit.MINUTES).build())
    // Synchronous calls leave OkHttp's dispatcher after headers, before body consumption.
    private val active = mutableSetOf<Call>()
    fun cancel() { synchronized(active) { active.toList() }.forEach { it.cancel() } }
    override fun execute(request: HttpRequest): HttpReply {
        if (request.url.scheme != "https") throw ProtocolException("HTTPS required")
        val body = request.body?.let { source -> object : RequestBody() {
            override fun contentType(): MediaType = request.mime.toMediaType()
            override fun contentLength(): Long = request.size
            override fun writeTo(sink: BufferedSink) { source().use { stream ->
                val buffer = ByteArray(64 * 1024)
                var size = 0L
                while (true) {
                    if (Thread.currentThread().isInterrupted) throw java.io.InterruptedIOException()
                    val count = stream.read(buffer)
                    if (count < 0) break
                    size += count
                    if (size > request.size) throw ProtocolException("Upload source changed size")
                    sink.write(buffer, 0, count)
                }
                if (size != request.size) throw ProtocolException("Upload source truncated")
            } }
        } }
        val builder = Request.Builder().url(request.url.toString()).method(request.method, body)
        request.headers.forEach { (key, value) -> builder.header(key, value) }
        val call = client.newCall(builder.build())
        synchronized(active) { active.add(call) }
        try {
            val response = call.execute()
            val stream = response.body?.byteStream() ?: ByteArrayInputStream(byteArrayOf())
            val owned = object : FilterInputStream(stream) {
                override fun close() {
                    try { response.close() } finally { synchronized(active) { active.remove(call) } }
                }
            }
            return HttpReply(response.code, response.headers.toMap(), cancellationChecked(owned) { call.isCanceled() })
        } catch (error: Exception) {
            synchronized(active) { active.remove(call) }
            throw error
        }
    }
}
/** Check after blocking reads as well, including EOF, so cancellation cannot report success. */
internal fun cancellationChecked(stream: InputStream, cancelled: () -> Boolean): InputStream = object : FilterInputStream(stream) {
    private fun checkCancellation() {
        if (Thread.currentThread().isInterrupted || cancelled()) {
            try { close() } finally { throw InterruptedIOException("HTTP operation cancelled") }
        }
    }
    override fun read(): Int {
        checkCancellation()
        val value = `in`.read()
        checkCancellation()
        return value
    }
    override fun read(bytes: ByteArray, offset: Int, length: Int): Int {
        checkCancellation()
        val count = `in`.read(bytes, offset, length)
        checkCancellation()
        return count
    }
    override fun skip(count: Long): Long {
        checkCancellation()
        val skipped = `in`.skip(count)
        checkCancellation()
        return skipped
    }
}
data class HttpRequest(val method: String, val url: URI, val headers: Map<String, String> = emptyMap(), val size: Long = 0,
    val mime: String = "application/octet-stream", val body: (() -> InputStream)? = null)
data class HttpReply(val code: Int, val headers: Map<String, String>, val body: InputStream) : Closeable {
    override fun close() { body.close() }
    fun header(name: String): String? = headers.entries.firstOrNull { it.key.equals(name, true) }?.value
}
fun interface HttpTransport { fun execute(request: HttpRequest): HttpReply }

class WebDavCloud(private val config: DavConfig, user: String, password: String,
    private val transport: HttpTransport = SecureHttpTransport()) : CloudStorage {
    private val auth: String
    private val endpoint: HttpUrl
    init {
        config.validate()
        if (user.isBlank() || user.contains(':') || user.any(Char::isISOControl) || password.isEmpty() || password.any { it == '\r' || it == '\n' }) throw ProtocolException("Invalid WebDAV credentials")
        auth = "Basic " + Base64.getEncoder().encodeToString("$user:$password".toByteArray(Charsets.UTF_8))
        endpoint = config.endpoint.trimEnd('/').toHttpUrl()
    }
    private fun url(parts: List<String>): URI {
        val builder = endpoint.newBuilder()
        parts.forEach { builder.addPathSegment(it) }
        return builder.build().toUri()
    }
    private fun call(method: String, parts: List<String>, headers: Map<String, String> = emptyMap(), size: Long = 0,
        mime: String = "application/octet-stream", body: (() -> InputStream)? = null): HttpReply {
        val response = transport.execute(HttpRequest(method, url(parts), headers + mapOf("Authorization" to auth, "Accept-Encoding" to "identity"), size, mime, body))
        if (response.code in 300..399 || response.header("Location") != null || response.header("Content-Location") != null) {
            response.close(); throw ProtocolException("Server redirect/reference rejected")
        }
        if (response.code == 401 || response.code == 403) { response.close(); throw AuthException() }
        return response
    }
    /** Depth zero inspection rejects WebDAV redirect resources and a response for a different path/origin. */
    private fun collection(parts: List<String>, create: Boolean): Boolean {
        val body = """<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/></d:prop></d:propfind>""".toByteArray()
        call("PROPFIND", parts, mapOf("Depth" to "0"), body.size.toLong(), "application/xml") { ByteArrayInputStream(body) }.use { response ->
            if (response.code == 404) {
                if (!create) return false
            } else {
                if (response.code != 207) throw ProtocolException("WebDAV folder inspection failed (HTTP ${response.code})")
                inspectCollection(readResponse(response, 256 * 1024), url(parts))
                return true
            }
        }
        call("MKCOL", parts, size = 0, body = { ByteArrayInputStream(byteArrayOf()) }).use { response ->
            if (response.code !in listOf(201, 405)) throw ProtocolException("Cannot create remote folder (HTTP ${response.code})")
        }
        return collection(parts, false).also { if (!it) throw ProtocolException("Remote folder missing after creation") }
    }
    private fun parents(path: String, create: Boolean): List<String>? {
        val full = safeRelativePath(config.folder) + safeRelativePath(path)
        // Check endpoint and every ancestor; no references/symlink-style redirect resources are followed.
        if (!collection(emptyList(), false)) throw ProtocolException("WebDAV endpoint does not exist")
        for (index in 1 until full.size) if (!collection(full.take(index), create)) return null
        return full
    }
    override fun ensureFolders(path: String) {
        val parts = safeRelativePath(config.folder) + safeRelativePath(path)
        if (!collection(emptyList(), false)) throw ProtocolException("WebDAV endpoint missing")
        for (index in 1..parts.size) collection(parts.take(index), true)
    }
    override fun read(path: String, limit: Int): ByteArray? {
        val parts = parents(path, false) ?: return null
        call("GET", parts).use { response ->
            if (response.code == 404) return null
            if (response.code != 200) throw ProtocolException("Download failed (HTTP ${response.code})")
            return readResponse(response, limit)
        }
    }
    override fun putImmutable(path: String, size: Long, mime: String, source: () -> InputStream) {
        if (size <= 0) throw ProtocolException("Empty upload rejected")
        val parts = parents(path, false) ?: throw ProtocolException("Upload folder missing")
        val expectedHash = source().use { digest(it, size) }
        fun identical(): Boolean = call("GET", parts).use { response ->
            if (response.code == 404) return@use false
            if (response.code != 200) throw ProtocolException("Cannot verify remote content (HTTP ${response.code})")
            val length = response.header("Content-Length")?.toLongOrNull()
            if (length != null && length != size) throw ConflictException()
            if (digest(response.body, size) != expectedHash) throw ConflictException()
            true
        }
        if (identical()) return
        call("PUT", parts, mapOf("If-None-Match" to "*"), size, mime, source).use { response ->
            if (response.code !in listOf(200, 201, 204, 412)) throw ProtocolException("Upload failed (HTTP ${response.code}); original preserved")
        }
        if (!identical()) throw ProtocolException("Upload not confirmed; retry later")
    }
    private fun readResponse(response: HttpReply, limit: Int): ByteArray {
        val length = response.header("Content-Length")?.let { it.toLongOrNull() ?: throw ProtocolException("Invalid response size") }
        if (length != null && (length < 0 || length > limit)) throw ProtocolException("Remote response exceeds size limit")
        val bytes = readBounded(response.body, limit)
        if (length != null && length != bytes.size.toLong()) throw ProtocolException("Truncated response")
        return bytes
    }
    private fun inspectCollection(bytes: ByteArray, expected: URI) {
        try {
            // Reject declarations before parsing; works on both Android and standard JVM XML parsers.
            val text = String(bytes, Charsets.UTF_8)
            if (text.contains("<!DOCTYPE", true) || text.contains("<!ENTITY", true)) throw ProtocolException("XML declarations rejected")
            val factory = DocumentBuilderFactory.newInstance().apply { isNamespaceAware = true; isExpandEntityReferences = false }
            val builder = factory.newDocumentBuilder()
            builder.setEntityResolver { _, _ -> throw ProtocolException("External XML rejected") }
            val doc = builder.parse(ByteArrayInputStream(bytes))
            if (doc.getElementsByTagNameNS("*", "redirectref").length > 0 || doc.getElementsByTagNameNS("*", "reftarget").length > 0 || doc.getElementsByTagNameNS("*", "symlink").length > 0) throw ProtocolException("Remote folder reference rejected")
            val responses = doc.getElementsByTagNameNS("DAV:", "response")
            if (responses.length != 1) throw ProtocolException("Ambiguous WebDAV folder")
            val element = responses.item(0) as org.w3c.dom.Element
            val hrefs = element.getElementsByTagNameNS("DAV:", "href")
            if (hrefs.length != 1) throw ProtocolException("Missing WebDAV folder identity")
            val resolved = expected.resolve(hrefs.item(0).textContent)
            if (resolved.scheme != expected.scheme || resolved.rawAuthority != expected.rawAuthority || resolved.query != null || resolved.fragment != null || resolved.path.trimEnd('/') != expected.path.trimEnd('/')) throw ProtocolException("Unexpected remote folder identity")
            val props = element.getElementsByTagNameNS("DAV:", "propstat")
            var valid = false
            for (index in 0 until props.length) {
                val prop = props.item(index) as org.w3c.dom.Element
                val status = prop.getElementsByTagNameNS("DAV:", "status")
                if (status.length == 1 && Regex("HTTP/[0-9.]+ 200(?: .*|$)").matches(status.item(0).textContent.trim()) && prop.getElementsByTagNameNS("DAV:", "collection").length == 1) valid = true
            }
            if (!valid) throw ProtocolException("Remote path is not a regular WebDAV collection")
        } catch (e: ProtocolException) { throw e }
        catch (_: Exception) { throw ProtocolException("Invalid WebDAV folder response") }
    }
}
