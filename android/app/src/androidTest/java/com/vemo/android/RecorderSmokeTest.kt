package com.vemo.android

import android.content.ComponentName
import android.content.Intent
import android.net.Uri
import android.test.InstrumentationTestCase
import com.vemo.android.core.*

@Suppress("DEPRECATION")
class RecorderSmokeTest : InstrumentationTestCase() {
    private fun shell(command: String) { instrumentation.uiAutomation.executeShellCommand(command).use { android.os.ParcelFileDescriptor.AutoCloseInputStream(it).readBytes() } }
    private fun command(action: String) {
        val intent = Intent().setComponent(ComponentName("com.vemo.android", "com.vemo.android.RecordingService")).setAction(action)
        instrumentation.targetContext.startService(intent)
    }
    private fun waitFor(store: RecordingStore, state: CaptureState, id: String? = null): Recording {
        for (attempt in 0..100) {
            store.list().firstOrNull { it.state == state && (id == null || id == it.id) }?.let { return it }
            Thread.sleep(100)
        }
        fail("Recording did not reach $state")
        throw AssertionError()
    }
    fun testForegroundPauseResumeStopPersistsAndSharesReadOnlyAudio() {
        shell("pm grant com.vemo.android android.permission.RECORD_AUDIO")
        shell("pm grant com.vemo.android android.permission.POST_NOTIFICATIONS")
        val ctx = instrumentation.targetContext
        val store = RecordingStore(java.io.File(ctx.filesDir, "recordings"))
        val activity = instrumentation.startActivitySync(Intent(Intent.ACTION_MAIN).setComponent(ComponentName("com.vemo.android", "com.vemo.android.MainActivity")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        try {
            command("com.vemo.START")
            val recording = waitFor(store, CaptureState.RECORDING)
            Thread.sleep(1500)
            command("com.vemo.PAUSE")
            val paused = waitFor(store, CaptureState.PAUSED, recording.id)
            assertEquals(recording.id, paused.id)
            assertFalse(paused.uploadable)
            assertFalse(java.io.File(store.audio(recording.id).parentFile, "result.json").exists())
            Thread.sleep(400)
            command("com.vemo.RESUME")
            waitFor(store, CaptureState.RECORDING, recording.id)
            Thread.sleep(1200)
            command("com.vemo.STOP")
            val complete = waitFor(store, CaptureState.COMPLETE, recording.id)
            assertEquals(recording.id, complete.id)
            assertTrue(complete.durationMs > 0)
            assertTrue(complete.bytes > 0)
            assertEquals(sha256(store.audio(complete.id)), complete.hash)
            assertEquals(complete, RecordingStore(java.io.File(ctx.filesDir, "recordings")).get(complete.id))
            val uri = Uri.parse("content://com.vemo.android.audio/${complete.id}")
            assertEquals("audio/mp4", ctx.contentResolver.getType(uri))
            ctx.contentResolver.openInputStream(uri)!!.use { assertTrue(it.readBytes().isNotEmpty()) }
            try { ctx.contentResolver.openFileDescriptor(uri, "w"); fail("Writable share accepted") } catch (_: java.io.FileNotFoundException) { }
            val share = ShareAudio.intent(ctx, complete)
            assertTrue(share.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
            assertNotNull(share.clipData)
        } finally {
            instrumentation.runOnMainSync { activity.finish() }
        }
    }
}
