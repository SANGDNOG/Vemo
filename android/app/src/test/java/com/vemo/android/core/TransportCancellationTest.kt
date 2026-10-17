package com.vemo.android.core

import okhttp3.*
import okio.Buffer
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.InterruptedIOException
import java.net.URI

class TransportCancellationTest {
    @Test fun cancellationAfterHeadersRejectsBodyEvenWhenDispatcherHasNoRunningCalls() {
        val bytes = Buffer().write(ByteArray(256 * 1024) { 120 })
        val client = OkHttpClient.Builder().addInterceptor { chain ->
            Response.Builder().request(chain.request()).protocol(Protocol.HTTP_1_1).code(200).message("OK")
                .body(object : ResponseBody() {
                    override fun contentType(): MediaType? = null
                    override fun contentLength() = 256L * 1024
                    override fun source() = bytes
                }).build()
        }.build()
        val transport = SecureHttpTransport(client)
        transport.execute(HttpRequest("GET", URI("https://www.googleapis.com/test"))).use { reply ->
            assertEquals(0, client.dispatcher.runningCallsCount())
            transport.cancel()
            assertThrows(InterruptedIOException::class.java) { readBounded(reply.body, 1024 * 1024) }
            assertEquals(256L * 1024, bytes.size)
        }
        // cancel applies to existing calls, not future retry attempts.
        transport.execute(HttpRequest("GET", URI("https://www.googleapis.com/test"))).use {
            assertEquals(120, it.body.read())
        }
    }
    @Test fun driveRejectsCancellationAtResponseHeadersAndClosesBody() {
        var cancelled = false
        var closed = false
        val api = GoogleDriveApi({ "synthetic" }, HttpTransport {
            cancelled = true
            HttpReply(200, emptyMap(), object : ByteArrayInputStream(Json.bytes(mapOf("user" to mapOf("permissionId" to "account")))) {
                override fun close() { closed = true; super.close() }
            })
        }) { cancelled }
        assertThrows(InterruptedIOException::class.java) { api.accountId() }
        assertTrue(closed)
    }
    @Test fun driveRejectsCancellationDuringBodyRead() {
        var cancelled = false
        var closed = false
        val api = GoogleDriveApi({ "synthetic" }, HttpTransport {
            HttpReply(200, emptyMap(), object : ByteArrayInputStream(Json.bytes(mapOf("user" to mapOf("permissionId" to "account")))) {
                override fun read(bytes: ByteArray, offset: Int, length: Int): Int {
                    val count = super.read(bytes, offset, length)
                    cancelled = true
                    return count
                }
                override fun close() { closed = true; super.close() }
            })
        }) { cancelled }
        assertThrows(InterruptedIOException::class.java) { api.accountId() }
        assertTrue(closed)
    }
}
