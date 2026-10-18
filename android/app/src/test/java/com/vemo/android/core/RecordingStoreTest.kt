package com.vemo.android.core

import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files

class RecordingStoreTest {
    @Test fun pauseDoesNotCompleteAndRestartKeepsInterruptedOriginal() {
        val dir = Files.createTempDirectory("vemo-store").toFile()
        try {
            val store = RecordingStore(dir)
            val item = store.create("Memo", Mode.RECORD_ONLY, null)
            store.partial(item.id).writeText("partial bytes")
            store.transition(item.id, CaptureState.PAUSED)
            assertEquals(CaptureState.PAUSED, store.get(item.id).state)
            assertFalse(store.get(item.id).uploadable)
            val restarted = RecordingStore(dir)
            restarted.recoverInterrupted()
            assertEquals(CaptureState.INTERRUPTED, restarted.get(item.id).state)
            assertEquals("partial bytes", restarted.partial(item.id).readText())
            assertFalse(restarted.get(item.id).uploadable)
        } finally { dir.deleteRecursively() }
    }
    @Test fun finalizationIsDurableAndRetainsStableIdentityAcrossRetry() {
        val dir = Files.createTempDirectory("vemo-store").toFile()
        try {
            val store = RecordingStore(dir)
            val item = store.create("Memo", Mode.RECORD_ONLY, null)
            store.partial(item.id).writeText("audio")
            store.transition(item.id, CaptureState.FINALIZING)
            val done = store.finish(item.id, 1200)
            val restored = RecordingStore(dir).get(item.id)
            assertTrue(restored.uploadable)
            assertEquals(done, restored)
            assertEquals("audio", store.audio(item.id).readText())
            assertEquals("6ed8919ce20490a5e3ad8630a4fab69475297abd07db73918dd5f36fcfaeb11b", restored.hash)
            store.hide(item.id)
            assertTrue(RecordingStore(dir).list().isEmpty())
            assertTrue(store.audio(item.id).exists())
        } finally { dir.deleteRecursively() }
    }
    @Test fun illegalTransitionsAndSymlinkAudioAreRejected() {
        val dir = Files.createTempDirectory("vemo-store").toFile()
        val external = Files.createTempFile("external", ".m4a").toFile().apply { writeText("untouched") }
        try {
            val store = RecordingStore(dir)
            val item = store.create("Memo", Mode.RECORD_ONLY, null)
            assertThrows(ProtocolException::class.java) { store.transition(item.id, CaptureState.COMPLETE) }
            Files.createSymbolicLink(store.partial(item.id).toPath(), external.toPath())
            store.transition(item.id, CaptureState.FINALIZING)
            assertThrows(ProtocolException::class.java) { store.finish(item.id, 1000) }
            assertEquals("untouched", external.readText())
        } finally { dir.deleteRecursively(); external.delete() }
    }
    @Test fun recordingAndSyncAreMutuallyExclusiveIncludingPause() {
        val gate = OperationGate()
        assertTrue(gate.beginRecording())
        assertFalse(gate.beginSync())
        assertFalse(gate.beginRecording())
        gate.endRecording()
        assertTrue(gate.beginSync())
        assertFalse(gate.beginRecording())
        gate.endSync()
        assertTrue(gate.beginRecording())
    }
}
