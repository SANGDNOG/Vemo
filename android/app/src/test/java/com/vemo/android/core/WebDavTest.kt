package com.vemo.android.core
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.net.URI

class WebDavTest {
    @Test fun conditionalRetryAcceptsOnlyIdenticalBytesAndNeverOverwritesConflict() {
        val transport = FakeDav()
        val cloud = WebDavCloud(DavConfig("https://example.com/dav", "Vemo"), "user", "secret", transport)
        cloud.ensureFolders("recordings/id/hash")
        cloud.putImmutable("recordings/id/hash/audio.m4a", 5, "audio/mp4") { ByteArrayInputStream("audio".toByteArray()) }
        cloud.putImmutable("recordings/id/hash/audio.m4a", 5, "audio/mp4") { ByteArrayInputStream("audio".toByteArray()) }
        assertThrows(ConflictException::class.java) {
            cloud.putImmutable("recordings/id/hash/audio.m4a", 5, "audio/mp4") { ByteArrayInputStream("other".toByteArray()) }
        }
        assertEquals("audio", String(transport.files.getValue("/dav/Vemo/recordings/id/hash/audio.m4a")))
        assertTrue(transport.conditionals.all { it == "*" })
    }
    @Test fun redirectsAndDavRedirectReferencesAreRejected() {
        val redirect = HttpTransport { request -> HttpReply(302, emptyMap(), ByteArrayInputStream(byteArrayOf())) }
        val cloud = WebDavCloud(DavConfig("https://example.com/dav", "Vemo"), "user", "secret", redirect)
        assertThrows(ProtocolException::class.java) { cloud.read("processor.json", Wire.MAX_JSON) }
        val link = HttpTransport { request ->
            HttpReply(207, emptyMap(), ByteArrayInputStream("""<d:multistatus xmlns:d="DAV:"><d:response><d:href>${request.url.path}/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/><d:redirectref/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>""".toByteArray()))
        }
        assertThrows(ProtocolException::class.java) { WebDavCloud(DavConfig("https://example.com/dav", "Vemo"), "user", "secret", link).ensureFolders("recordings") }
    }
    @Test fun authenticationAndTruncatedBodiesRemainFailures() {
        val transport = HttpTransport { HttpReply(401, emptyMap(), ByteArrayInputStream(byteArrayOf())) }
        assertThrows(AuthException::class.java) { WebDavCloud(DavConfig("https://example.com/dav", "Vemo"), "u", "p", transport).read("processor.json", Wire.MAX_JSON) }
        val truncated = HttpTransport { HttpReply(200, mapOf("Content-Length" to "8"), ByteArrayInputStream("short".toByteArray())) }
        assertThrows(ProtocolException::class.java) { WebDavCloud(DavConfig("https://example.com/dav", "Vemo"), "u", "p", truncated).read("processor.json", Wire.MAX_JSON) }
    }
    private class FakeDav : HttpTransport {
        val files = mutableMapOf<String, ByteArray>()
        val conditionals = mutableListOf<String?>()
        override fun execute(request: HttpRequest): HttpReply {
            val path = request.url.path
            fun response(code: Int, bytes: ByteArray = byteArrayOf()) = HttpReply(code, mapOf("Content-Length" to bytes.size.toString()), ByteArrayInputStream(bytes))
            return when (request.method) {
                "PROPFIND" -> response(207, """<d:multistatus xmlns:d="DAV:"><d:response><d:href>$path/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>""".toByteArray())
                "GET" -> files[path]?.let { response(200, it) } ?: response(404)
                "PUT" -> {
                    conditionals += request.headers["If-None-Match"]
                    if (path in files) response(412) else {
                        files[path] = request.body!!.invoke().use { it.readBytes() }
                        response(201)
                    }
                }
                else -> response(201)
            }
        }
    }
}
