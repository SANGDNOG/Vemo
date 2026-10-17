package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.util.UUID

class LibraryStoreTest {
    private fun withFile(test: (File) -> Unit) {
        val dir = Files.createTempDirectory("vemo-library").toFile()
        try { test(File(dir, "library.json")) } finally { dir.deleteRecursively() }
    }
    @Test fun freshAndNestedMembershipSurviveReopenAndRename() = withFile { file ->
        val store = LibraryStore(file)
        assertTrue(store.snapshot().folders.isEmpty())
        assertFalse(file.exists())
        val root = store.createFolder(" Work ")
        val child = store.createFolder("회의", root.id)
        val id = UUID.randomUUID().toString()
        store.assign(id, child.id)
        store.renameFolder(root.id, "Projects")
        val reopened = LibraryStore(file)
        assertEquals("Projects/회의", reopened.path(child.id))
        assertEquals(child.id, reopened.folderFor(id))
        assertEquals("Projects", reopened.snapshot().folders.first { it.id == root.id }.name)
        reopened.assign(id, null)
        assertNull(LibraryStore(file).folderFor(id))
    }
    @Test fun invalidOperationsPreserveLastGoodState() = withFile { file ->
        val store = LibraryStore(file)
        val root = store.createFolder("Work")
        val other = store.createFolder("Other")
        store.createFolder("Work", other.id)
        val before = file.readBytes()
        for (operation in listOf<() -> Unit>(
            { store.createFolder(" Work ") }, { store.createFolder(" ") },
            { store.createFolder("Bad", UUID.randomUUID().toString()) },
            { store.renameFolder(other.id, "Work") }, { store.renameFolder("bad", "Name") },
            { store.assign("bad", root.id) },
            { store.assign(UUID.randomUUID().toString(), UUID.randomUUID().toString()) },
            { store.deleteFolder(UUID.randomUUID().toString()) }
        )) {
            assertThrows(ProtocolException::class.java) { operation() }
            assertArrayEquals(before, file.readBytes())
        }
    }
    @Test fun descendantDeletionUnfilesOnlyAffectedRecordingsWithoutChangingAudioOrMarker() = withFile { file ->
        val recordings = RecordingStore(File(file.parentFile, "recordings"))
        val item = recordings.create("Memo", Mode.RECORD_ONLY, null)
        recordings.partial(item.id).writeText("immutable audio")
        recordings.transition(item.id, CaptureState.FINALIZING)
        recordings.finish(item.id, 1000)
        val local = File(file.parentFile, "recordings/${item.id}/local.json")
        val marker = local.readBytes()
        val wireMarker = Wire.marker(recordings.get(item.id))
        val audio = recordings.audio(item.id).readBytes()
        val store = LibraryStore(file)
        val root = store.createFolder("Work")
        val child = store.createFolder("Meetings", root.id)
        val grandchild = store.createFolder("October", child.id)
        val keep = store.createFolder("Personal")
        val otherId = UUID.randomUUID().toString()
        store.assign(item.id, grandchild.id)
        store.assign(otherId, keep.id)
        store.deleteFolder(root.id)
        val reopened = LibraryStore(file)
        assertEquals(listOf(keep), reopened.snapshot().folders)
        assertNull(reopened.folderFor(item.id))
        assertEquals(keep.id, reopened.folderFor(otherId))
        assertArrayEquals(marker, local.readBytes())
        assertArrayEquals(wireMarker, Wire.marker(recordings.get(item.id)))
        assertArrayEquals(audio, recordings.audio(item.id).readBytes())
    }
    @Test fun corruptFutureAndInvalidGraphsArePreservedAndSurfaced() = withFile { file ->
        val a = "11111111-1111-4111-8111-111111111111"
        val b = "22222222-2222-4222-8222-222222222222"
        val fixtures = listOf(
            "{broken", "null", "[]",
            """{"version":2,"folders":[],"recordingFolders":{}}""",
            """{"version":"1","folders":[],"recordingFolders":{}}""",
            """{"version":1,"folders":[{"id":"$a","name":3,"parentId":null}],"recordingFolders":{}}""",
            """{"version":1,"folders":[{"id":"bad","name":"A","parentId":null}],"recordingFolders":{}}""",
            """{"version":1,"folders":[{"id":"$a","name":"A","parentId":"$b"}],"recordingFolders":{}}""",
            """{"version":1,"folders":[{"id":"$a","name":"A","parentId":"$b"},{"id":"$b","name":"B","parentId":"$a"}],"recordingFolders":{}}""",
            """{"version":1,"folders":[{"id":"$a","name":"A","parentId":null},{"id":"$b","name":"A","parentId":null}],"recordingFolders":{}}""",
            """{"version":1,"folders":[],"recordingFolders":{"$a":7}}""",
            """{"version":1,"folders":[],"recordingFolders":{"$a":"$b"}}"""
        )
        for (text in fixtures) {
            file.writeText(text)
            val store = LibraryStore(file)
            assertThrows(ProtocolException::class.java) { store.snapshot() }
            assertThrows(ProtocolException::class.java) { store.createFolder("Replacement") }
            assertEquals(text, file.readText())
        }
    }
    @Test fun externalCorruptionCannotBeOverwrittenByAnExistingInstance() = withFile { file ->
        val store = LibraryStore(file)
        store.createFolder("Good")
        file.writeText("broken")
        assertThrows(ProtocolException::class.java) { store.createFolder("Replacement") }
        assertEquals("broken", file.readText())
    }
    @Test fun failedAtomicReplacementPreservesRecordingAndLeavesNoTemporaryFiles() = withFile { file ->
        val store = LibraryStore(file)
        store.createFolder("Good")
        val original = File(file.parentFile, "original.json")
        file.renameTo(original)
        Files.createSymbolicLink(file.toPath(), original.toPath())
        val before = original.readBytes()
        assertThrows(ProtocolException::class.java) { store.createFolder("Unsafe") }
        assertArrayEquals(before, original.readBytes())
        assertFalse(file.parentFile!!.listFiles()!!.any { it.name.startsWith(".metadata-") })
    }
}
