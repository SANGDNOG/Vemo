package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import okhttp3.HttpUrl.Companion.toHttpUrl
import java.io.ByteArrayInputStream
import java.io.IOException

class GoogleDriveTest {
    private val processor = "11111111-1111-4111-8111-111111111111"
    private val binding = DriveBinding("account", "root", processor)
    private class Reservations : DriveReservations {
        val ids = mutableMapOf<String, String>()
        override fun get(key: String) = ids[key]
        override fun save(key: String, id: String) { ids[key] = id }
    }
    private fun api(fake: FakeDrive) = GoogleDriveApi({ "synthetic-token" }, fake)
    @Test fun discoveryUsesSharedPropertiesAndExplicitRootSelection() {
        val fake = FakeDrive()
        assertEquals(listOf(DriveRoot("root", "Vemo", processor)), api(fake).discoverRoots())
        assertTrue(fake.requests.single().url.query.contains("properties"))
        assertFalse(fake.requests.single().url.query.contains("appProperties"))
        assertEquals("account", api(fake).accountId())
    }
    @Test fun duplicateNamesOrProcessorsAndWrongRootMetadataAreRejected() {
        for (change in listOf("duplicate", "mime", "protocol", "processor")) {
            val fake = FakeDrive()
            when (change) {
                "duplicate" -> fake.roots += fake.roots.single().toMutableMap().apply { put("id", "second") }
                "mime" -> fake.roots[0]["mimeType"] = "application/json"
                "protocol" -> fake.roots[0]["properties"] = mapOf("vemoProtocol" to "2", "vemoKind" to "root", "vemoProcessor" to processor)
                "processor" -> fake.roots[0]["properties"] = mapOf("vemoProtocol" to "1", "vemoKind" to "root", "vemoProcessor" to "bad")
            }
            assertThrows(ProtocolException::class.java) { api(fake).discoverRoots() }
        }
    }
    @Test fun accountRootAndProcessorMismatchPreventWrites() {
        for (change in listOf("account", "root", "processor")) {
            val fake = FakeDrive()
            val value = when (change) {
                "account" -> binding.copy(accountId = "another")
                "root" -> binding.copy(rootId = "another")
                else -> binding.copy(processorId = "22222222-2222-4222-8222-222222222222")
            }
            assertThrows(ProtocolException::class.java) { GoogleDriveCloud(api(fake), value, Reservations()) }
            assertTrue(fake.requests.none { it.method == "POST" })
        }
        assertNotEquals(binding.identity(), binding.copy(accountId = "another").identity())
        assertNotEquals(binding.identity(), binding.copy(rootId = "another").identity())
        assertNotEquals(binding.identity(), binding.copy(processorId = "22222222-2222-4222-8222-222222222222").identity())
    }
    @Test fun descendantsRequireMatchingMimeParentNameAndPublicPathHash() {
        for (change in listOf("mime", "parent", "name", "key", "protocol", "duplicate")) {
            val fake = FakeDrive()
            val file = fake.add("result", "result.json", "root", "application/json", "result.json", "{}".toByteArray())
            when (change) {
                "mime" -> file["mimeType"] = "application/vnd.google-apps.shortcut"
                "parent" -> file["parents"] = listOf("wrong")
                "name" -> file["name"] = "other.json"
                "key" -> file["properties"] = mapOf("vemoProtocol" to "1", "vemoKey" to "bad")
                "protocol" -> file["properties"] = mapOf("vemoProtocol" to "2", "vemoKey" to drivePathKey("result.json"))
                "duplicate" -> fake.files["duplicate"] = file.toMutableMap().apply { put("id", "duplicate") }
            }
            // Deliberately malicious listings include an object even if its parent/name don't match query.
            fake.forceListing = true
            assertThrows(ConflictException::class.java) { GoogleDriveCloud(api(fake), binding, Reservations()).read("result.json", 100) }
        }
    }
    @Test fun unicodePathKeyHashesUtf8AndDoesNotPublishFullPath() {
        val path = "recordings/한글/audio.m4a"
        assertEquals(digest(ByteArrayInputStream(path.toByteArray(Charsets.UTF_8))), drivePathKey(path))
        assertEquals(64, drivePathKey(path).length)
        assertThrows(ProtocolException::class.java) { drivePathKey("recordings/../audio.m4a") }
    }
    @Test fun lostAckAndDelayedSearchReuseDurablyReservedIdWithoutOverwrite() {
        val fake = FakeDrive()
        val reservations = Reservations()
        val cloud = GoogleDriveCloud(api(fake), binding, reservations)
        val data = "audio".toByteArray()
        fake.loseAck = true
        assertThrows(IOException::class.java) { cloud.putImmutable("audio.m4a", 5, "audio/mp4") { ByteArrayInputStream(data) } }
        assertEquals(1, reservations.ids.size)
        fake.hideFilesFromSearch = true
        cloud.putImmutable("audio.m4a", 5, "audio/mp4") { ByteArrayInputStream(data) }
        assertEquals(1, fake.creates)
        assertArrayEquals(data, fake.content.getValue("generated1"))
        assertThrows(ConflictException::class.java) { cloud.putImmutable("audio.m4a", 5, "audio/mp4") { ByteArrayInputStream("other".toByteArray()) } }
        assertEquals(1, fake.creates)
        assertTrue(fake.requests.none { it.method in setOf("PATCH", "DELETE") })
    }
    @Test fun reservationFailureOccursBeforeCreate() {
        val fake = FakeDrive()
        val reservations = object : DriveReservations {
            override fun get(key: String): String? = null
            override fun save(key: String, id: String) { throw IOException("disk full") }
        }
        val cloud = GoogleDriveCloud(api(fake), binding, reservations)
        assertThrows(IOException::class.java) { cloud.ensureFolders("recordings") }
        assertEquals(0, fake.creates)
    }
    @Test fun folderRetriesUseSameIdentityAndNeverCreateRoot() {
        val fake = FakeDrive()
        val reservations = Reservations()
        val cloud = GoogleDriveCloud(api(fake), binding, reservations)
        fake.loseAck = true
        assertThrows(IOException::class.java) { cloud.ensureFolders("recordings") }
        fake.hideFilesFromSearch = true
        cloud.ensureFolders("recordings")
        assertEquals(1, fake.creates)
        assertEquals(listOf("root"), fake.files.getValue("generated1")["parents"])
        val props = fake.files.getValue("generated1")["properties"] as Map<*, *>
        assertEquals(mapOf("vemoProtocol" to "1", "vemoKey" to drivePathKey("recordings")), props)
    }
    @Test fun jsonTruncationSizeLimitsAndPartialUploadNeverSucceed() {
        val fake = FakeDrive()
        val file = fake.add("result", "result.json", "root", "application/json", "result.json", "{}".toByteArray())
        val cloud = GoogleDriveCloud(api(fake), binding, Reservations())
        assertArrayEquals("{}".toByteArray(), cloud.read("result.json", 10))
        file["size"] = "10"
        assertThrows(ProtocolException::class.java) { cloud.read("result.json", 10) }
        assertThrows(ProtocolException::class.java) { cloud.putImmutable("audio.m4a", 16L * 1024 * 1024 * 1024 + 1, "audio/mp4") { ByteArrayInputStream(byteArrayOf()) } }
        assertThrows(ConflictException::class.java) { cloud.putImmutable("audio.m4a", 10, "audio/mp4") { ByteArrayInputStream(byteArrayOf(1)) } }
        fake.partialUpload = true
        assertThrows(ConflictException::class.java) { cloud.putImmutable("audio.m4a", 5, "audio/mp4") { ByteArrayInputStream("audio".toByteArray()) } }
    }
    @Test fun authRedirectsPaginationAndIncompleteSearchFailClosed() {
        for (code in listOf(401, 403, 302)) {
            var calls = 0
            val api = GoogleDriveApi({ "synthetic" }, HttpTransport { calls++; HttpReply(code, emptyMap(), ByteArrayInputStream(byteArrayOf())) })
            assertThrows(if (code == 302) ProtocolException::class.java else AuthException::class.java) { api.accountId() }
            assertEquals(1, calls)
        }
        for (body in listOf(mapOf("files" to emptyList<Any>(), "incompleteSearch" to true), mapOf("files" to emptyList<Any>(), "nextPageToken" to "repeated"))) {
            var calls = 0
            val api = GoogleDriveApi({ "synthetic" }, HttpTransport { calls++; HttpReply(200, emptyMap(), ByteArrayInputStream(Json.bytes(body))) })
            assertThrows(ProtocolException::class.java) { api.discoverRoots() }
            assertTrue(calls <= 100)
        }
    }
    @Test fun resumableSessionReferenceRejectsForeignHostsPathsAndDuplicateParameters() {
        for (location in listOf("https://evil.example/upload/drive/v3/files?uploadType=resumable&upload_id=x",
            "http://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=x",
            "https://www.googleapis.com/other?uploadType=resumable&upload_id=x",
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=x&upload_id=y")) {
            val fake = FakeDrive().apply { sessionLocation = location }
            val cloud = GoogleDriveCloud(api(fake), binding, Reservations())
            assertThrows(ProtocolException::class.java) { cloud.putImmutable("audio.m4a", 5, "audio/mp4") { ByteArrayInputStream("audio".toByteArray()) } }
            assertTrue(fake.requests.none { it.method == "PUT" })
        }
    }
    @Test fun cancellationStopsRequestsWithoutOpeningAuthorizationOrWriting() {
        val fake = FakeDrive()
        val api = GoogleDriveApi({ "synthetic" }, fake) { true }
        assertThrows(java.io.InterruptedIOException::class.java) { api.accountId() }
        assertTrue(fake.requests.isEmpty())
    }
    @Test fun recordingBindingRemainsImmutableAcrossAccountRootAndProcessorChanges() {
        val dir = java.nio.file.Files.createTempDirectory("drive-binding").toFile()
        try {
            val store = RecordingStore(dir)
            val record = store.create("Synthetic", Mode.DESKTOP, null)
            store.update(record.id) { it.copy(cloudKey = binding.identity(), targetProcessor = processor) }
            for (different in listOf(binding.copy(accountId = "different"), binding.copy(rootId = "different"),
                binding.copy(processorId = "22222222-2222-4222-8222-222222222222"))) {
                assertThrows(ProtocolException::class.java) { store.update(record.id) { it.copy(cloudKey = different.identity()) } }
            }
            assertThrows(ProtocolException::class.java) { store.update(record.id) { it.copy(targetProcessor = "22222222-2222-4222-8222-222222222222") } }
            assertEquals(binding.identity(), RecordingStore(dir).get(record.id).cloudKey)
        } finally { dir.deleteRecursively() }
    }
    @Test fun audioMustBeConfirmedBeforeCompletionMarkerIsPublished() {
        val dir = java.nio.file.Files.createTempDirectory("drive-marker").toFile()
        try {
            val store = RecordingStore(dir)
            val record = store.create("Synthetic", Mode.DESKTOP, processor)
            store.partial(record.id).writeText("audio")
            store.transition(record.id, CaptureState.FINALIZING)
            val finalized = store.finish(record.id, 1000)
            val fake = FakeDrive()
            val cloud = GoogleDriveCloud(api(fake), binding, Reservations())
            fake.partialUpload = true
            assertThrows(ConflictException::class.java) { SyncEngine(cloud).upload(finalized, store.audio(record.id)) }
            assertTrue(fake.files.values.none { it["name"] == "recording.json" })
            // A separate valid destination demonstrates the ordering for a completed upload.
            val complete = FakeDrive()
            SyncEngine(GoogleDriveCloud(api(complete), binding, Reservations())).upload(finalized, store.audio(record.id))
            val names = complete.files.values.map { it["name"] }
            assertTrue("audio.m4a" in names && "recording.json" in names)
            val writes = complete.requests.filter { it.method == "PUT" }
            assertEquals(2, writes.size)
            assertEquals("audio/mp4", writes[0].mime)
            assertEquals("application/json", writes[1].mime)
        } finally { dir.deleteRecursively() }
    }
    private class FakeDrive : HttpTransport {
        val requests = mutableListOf<HttpRequest>()
        val roots = mutableListOf(mutableMapOf<String, Any>("id" to "root", "name" to "Vemo", "mimeType" to "application/vnd.google-apps.folder", "trashed" to false,
            "properties" to mapOf("vemoProtocol" to "1", "vemoKind" to "root", "vemoProcessor" to "11111111-1111-4111-8111-111111111111")))
        val files = mutableMapOf<String, MutableMap<String, Any>>()
        val content = mutableMapOf<String, ByteArray>()
        var forceListing = false
        var hideFilesFromSearch = false
        var loseAck = false
        var partialUpload = false
        var creates = 0
        var session: Map<String, Any>? = null
        var sessionLocation = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=synthetic"
        fun add(id: String, name: String, parent: String, mime: String, path: String, data: ByteArray): MutableMap<String, Any> {
            val file = mutableMapOf<String, Any>("id" to id, "name" to name, "parents" to listOf(parent), "mimeType" to mime, "trashed" to false,
                "properties" to mapOf("vemoProtocol" to "1", "vemoKey" to drivePathKey(path)), "size" to data.size.toString())
            files[id] = file; content[id] = data
            return file
        }
        override fun execute(request: HttpRequest): HttpReply {
            requests += request
            assertEquals("https", request.url.scheme)
            assertEquals("www.googleapis.com", request.url.host)
            fun reply(code: Int, value: Any = emptyMap<String, Any>()) = HttpReply(code, emptyMap(), ByteArrayInputStream(Json.bytes(value)))
            val url = request.url.toString().toHttpUrl()
            fun finish(file: MutableMap<String, Any>): HttpReply {
                files[file.getValue("id").toString()] = file
                if (loseAck) { loseAck = false; throw IOException("synthetic lost ack") }
                return reply(201, file)
            }
            if (request.method == "PUT") {
                val file = session!!.toMutableMap()
                val data = request.body!!.invoke().use { it.readBytes() }
                assertEquals(request.size, data.size.toLong())
                val id = file.getValue("id").toString()
                content[id] = if (partialUpload) data.copyOf(1) else data
                file["size"] = content.getValue(id).size.toString()
                return finish(file)
            }
            if (request.method == "POST") {
                creates++
                val bytes = request.body!!.invoke().use { it.readBytes() }
                assertEquals(request.size, bytes.size.toLong())
                val meta = Json.parse(bytes)
                val id = Json.string(meta, "id")
                val properties = meta.getAsJsonObject("properties").entrySet().associate { it.key to it.value.asString }
                val file = mutableMapOf<String, Any>("id" to id, "name" to Json.string(meta, "name"), "mimeType" to Json.string(meta, "mimeType"),
                    "parents" to meta.getAsJsonArray("parents").map { it.asString }, "properties" to properties, "trashed" to false)
                if (request.url.path.startsWith("/upload")) {
                    session = file
                    return HttpReply(200, mapOf("Location" to sessionLocation), ByteArrayInputStream(byteArrayOf()))
                }
                return finish(file)
            }
            return when (request.url.path) {
                "/drive/v3/about" -> reply(200, mapOf("user" to mapOf("permissionId" to "account")))
                "/drive/v3/files/generateIds" -> reply(200, mapOf("ids" to listOf("generated${creates + 1}")))
                "/drive/v3/files" -> {
                    val query = url.queryParameter("q")!!
                    val candidates = if (query.contains("vemoKind")) roots else if (hideFilesFromSearch) emptyList() else files.values.filter { file ->
                        forceListing || (query.contains("'${(file["parents"] as List<*>)[0]}' in parents") && query.contains("name = '${file["name"]}'"))
                    }
                    reply(200, mapOf("files" to candidates))
                }
                else -> {
                    val id = request.url.path.substringAfterLast('/')
                    if (url.queryParameter("alt") == "media") HttpReply(200, emptyMap(), ByteArrayInputStream(content.getValue(id)))
                    else (roots.firstOrNull { it["id"] == id } ?: files[id])?.let { reply(200, it) } ?: reply(404)
                }
            }
        }
    }
}
