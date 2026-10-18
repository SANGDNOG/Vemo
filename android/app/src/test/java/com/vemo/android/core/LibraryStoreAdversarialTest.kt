package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.util.UUID

/** Independent boundary checks complement the implementation owner's ordinary API tests. */
class LibraryStoreAdversarialTest {
    private fun withFile(test: (File) -> Unit) {
        val root = Files.createTempDirectory("vemo-library-adversarial-").toFile()
        try { test(File(root, "library.json")) } finally { root.deleteRecursively() }
    }

    @Test fun deeplyNestedMetadataCanReopenResolveAndDeleteWithoutStackOverflow() = withFile { file ->
        val folders = mutableListOf<LibraryFolder>()
        repeat(2000) { index ->
            folders += LibraryFolder(UUID.randomUUID().toString(), "Folder $index", folders.lastOrNull()?.id)
        }
        val recording = UUID.randomUUID().toString()
        file.writeBytes(Json.bytes(mapOf("version" to 1, "folders" to folders,
            "recordingFolders" to mapOf(recording to folders.last().id))))
        val store = LibraryStore(file)
        assertEquals(2000, store.snapshot().folders.size)
        assertEquals(folders.joinToString("/") { it.name }, store.path(folders.last().id))
        assertEquals(folders.last().id, store.folderFor(recording))
        store.deleteFolder(folders.first().id)
        assertEquals(LibraryState(emptyList(), emptyMap()), LibraryStore(file).snapshot())
    }

    @Test fun malformedMetadataBlocksEveryMutationAndPreservesExactBytes() = withFile { file ->
        val folder = UUID.randomUUID().toString()
        val recording = UUID.randomUUID().toString()
        val inputs = listOf(
            """{"version":1,"folders":null,"recordingFolders":{}}""",
            """{"version":1,"folders":[],"recordingFolders":null}""",
            """{"version":1,"folders":[{"id":"$folder","name":"Folder","parentId":false}],"recordingFolders":{}}""",
            """{"version":1,"folders":[{"id":"$folder","name":"Folder","parentId":null}],"recordingFolders":{"$recording":[]}}""",
            """{"version":1,"version":1,"folders":[],"recordingFolders":{}}""",
            """{"version":1,"folders":[{"id":"$folder","name":"Folder","parentId":null},{"id":"$folder","name":"Other","parentId":null}],"recordingFolders":{}}"""
        ).map { it.toByteArray() } + listOf(byteArrayOf(0xc3.toByte(), 0x28))
        for (bytes in inputs) {
            file.writeBytes(bytes)
            val store = LibraryStore(file)
            val operations = listOf<() -> Unit>(
                { store.snapshot() }, { store.createFolder("Replacement") },
                { store.renameFolder(folder, "Renamed") }, { store.deleteFolder(folder) },
                { store.assign(recording, folder) }, { store.assign(recording, null) }
            )
            for (operation in operations) {
                assertThrows(ProtocolException::class.java) { operation() }
                assertArrayEquals(bytes, file.readBytes())
            }
        }
    }

    @Test fun oversizedMetadataRemainsOnDiskAfterReadAndWriteRejection() = withFile { file ->
        val bytes = ByteArray(Wire.MAX_JSON + 1) { ' '.code.toByte() }
        file.writeBytes(bytes)
        val store = LibraryStore(file)
        assertThrows(ProtocolException::class.java) { store.snapshot() }
        assertThrows(ProtocolException::class.java) { store.createFolder("Replacement") }
        assertArrayEquals(bytes, file.readBytes())
    }

    @Test fun symlinkAncestorCannotRedirectLibraryWrites() = withFile { file ->
        val actual = File(file.parentFile, "actual").apply { mkdir() }
        val alias = File(file.parentFile, "alias")
        Files.createSymbolicLink(alias.toPath(), actual.toPath())
        val original = File(actual, "library.json")
        original.writeText("preserve external content")
        val store = LibraryStore(File(alias, "library.json"))
        assertThrows(ProtocolException::class.java) { store.snapshot() }
        assertThrows(ProtocolException::class.java) { store.createFolder("Unsafe") }
        assertEquals("preserve external content", original.readText())
        alias.delete()
    }
}
