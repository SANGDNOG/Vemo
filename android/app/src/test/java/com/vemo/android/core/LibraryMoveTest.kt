package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.util.UUID

class LibraryMoveTest {
    private fun withStore(test: (LibraryStore, File) -> Unit) {
        val dir = Files.createTempDirectory("vemo-library-move").toFile()
        try { val file = File(dir, "library.json"); test(LibraryStore(file), file) }
        finally { dir.deleteRecursively() }
    }

    @Test fun moveSubtreeThenReturnToRootPreservesIdsMembershipAndAudio() = withStore { store, file ->
        val a = store.createFolder("회의")
        val b = store.createFolder("프로젝트")
        val child = store.createFolder("설계", a.id)
        val recordings = RecordingStore(File(file.parentFile, "recordings"))
        val item = recordings.create("원본", Mode.RECORD_ONLY, null)
        recordings.partial(item.id).writeText("immutable audio fixture")
        recordings.transition(item.id, CaptureState.FINALIZING)
        val complete = recordings.finish(item.id, 1000)
        store.assign(item.id, child.id)
        val local = File(recordings.audio(item.id).parentFile, "local.json")
        val bytes = local.readBytes()
        val audio = recordings.audio(item.id).readBytes()
        val marker = Wire.marker(complete)
        store.moveFolder(a.id, b.id)
        val reopened = LibraryStore(file)
        assertEquals("프로젝트/회의/설계", reopened.path(child.id))
        assertEquals(child.id, reopened.folderFor(item.id))
        assertEquals(child, reopened.snapshot().folders.single { it.id == child.id })
        reopened.moveFolder(a.id, null)
        assertEquals("회의/설계", LibraryStore(file).path(child.id))
        assertArrayEquals(bytes, local.readBytes())
        assertArrayEquals(audio, recordings.audio(item.id).readBytes())
        assertArrayEquals(marker, Wire.marker(recordings.get(item.id)))
    }

    @Test fun selfDescendantMissingAndDuplicateMovesPreserveExactSidecar() = withStore { store, file ->
        val a = store.createFolder("회의")
        val b = store.createFolder("프로젝트")
        val child = store.createFolder("설계", a.id)
        val grandchild = store.createFolder("검토", child.id)
        store.createFolder("회의", b.id)
        val before = file.readBytes()
        for (move in listOf<() -> Unit>(
            { store.moveFolder(a.id, a.id) }, { store.moveFolder(a.id, child.id) },
            { store.moveFolder(a.id, grandchild.id) }, { store.moveFolder(a.id, b.id) },
            { store.moveFolder(a.id, UUID.randomUUID().toString()) },
            { store.moveFolder(UUID.randomUUID().toString(), b.id) },
            { store.moveFolder("invalid", null) }
        )) {
            assertThrows(ProtocolException::class.java) { move() }
            assertArrayEquals(before, file.readBytes())
        }
    }

    @Test fun unchangedParentIsNoOpAndRootDuplicateIsRejected() = withStore { store, file ->
        store.createFolder("회의")
        val b = store.createFolder("프로젝트")
        val child = store.createFolder("회의", b.id)
        val before = file.readBytes()
        store.moveFolder(child.id, b.id)
        assertArrayEquals(before, file.readBytes())
        assertThrows(ProtocolException::class.java) { store.moveFolder(child.id, null) }
        assertArrayEquals(before, file.readBytes())
    }

    @Test fun corruptedMetadataCannotBeReplacedByMove() = withStore { store, file ->
        val folder = store.createFolder("회의")
        file.writeText("{broken")
        assertThrows(ProtocolException::class.java) { store.moveFolder(folder.id, null) }
        assertEquals("{broken", file.readText())
    }
}
