package com.vemo.android

import android.app.Activity
import android.app.Application
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.os.ParcelFileDescriptor
import android.os.Bundle
import android.os.SystemClock
import android.test.InstrumentationTestCase
import android.view.InputDevice
import android.view.MotionEvent
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.view.accessibility.AccessibilityNodeInfo
import android.widget.EditText
import android.widget.TextView
import com.vemo.android.core.*
import java.io.File
import java.util.UUID

/** Uses only platform instrumentation. Never clears application data or unrelated recordings. */
@Suppress("DEPRECATION")
class LibraryUiTest : InstrumentationTestCase() {
    private val prefix = "LibraryUi-${UUID.randomUUID().toString().take(8)}"
    private val ownedRecordings = linkedSetOf<String>()
    private val ownedFolders = linkedSetOf<String>()
    private lateinit var app: VemoApp
    private var captureId: String? = null
    @Volatile private var activity: Activity? = null
    private val lifecycle = object : Application.ActivityLifecycleCallbacks {
        override fun onActivityResumed(value: Activity) { if (value is MainActivity) activity = value }
        override fun onActivityCreated(value: Activity, state: Bundle?) = Unit
        override fun onActivityStarted(value: Activity) = Unit
        override fun onActivityPaused(value: Activity) = Unit
        override fun onActivityStopped(value: Activity) = Unit
        override fun onActivitySaveInstanceState(value: Activity, state: Bundle) = Unit
        override fun onActivityDestroyed(value: Activity) = Unit
    }

    override fun setUp() {
        super.setUp()
        app = instrumentation.targetContext.applicationContext as VemoApp
        assertFalse("Validation requires an idle app; do not interrupt another capture", app.gate.busy())
        app.registerActivityLifecycleCallbacks(lifecycle)
    }

    override fun tearDown() {
        try {
            captureId?.let { id ->
                if (app.store.get(id).state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) {
                    command("STOP")
                    await("Test capture did not stop") { app.store.get(id).state !in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING) }
                }
                await("Capture gate did not release") { !app.gate.isRecording() }
            }
            activity?.let { current -> onMain { current.finish() } }
            instrumentation.waitForIdleSync()
            for (id in ownedRecordings) {
                app.library.assign(id, null)
                val folder = File(app.filesDir, "recordings/$id")
                check(ownedRecordings.contains(folder.name))
                assertTrue("Cannot remove test recording $id", !folder.exists() || folder.deleteRecursively())
            }
            for (id in ownedFolders.toList().asReversed()) {
                if (app.library.snapshot().folders.any { it.id == id }) app.library.deleteFolder(id)
            }
        } finally {
            app.unregisterActivityLifecycleCallbacks(lifecycle)
            super.tearDown()
        }
    }

    fun testInitialLibraryLandingPreservesExistingData() {
        val before = app.store.list()
        launch()
        assertLibrary()
        assertNoText("녹음 시작")
        assertEquals(before, app.store.list())
        if (before.isEmpty()) screenshot("library-empty")
    }

    fun testFolderCreateNestedRenameMovePersistDeletePreservesRecordings() {
        val first = fixture("주간 기획 회의")
        val second = fixture("서비스 디자인 검토")
        val third = fixture("아이디어 메모")
        val before = listOf(first, second, third).associate { item ->
            item.id to Triple(app.store.audio(item.id).readBytes(), File(app.store.audio(item.id).parentFile, "local.json").readBytes(), Wire.marker(item))
        }
        launch()
        assertLibrary()
        click("＋ 폴더"); setDialogText("검증 프로젝트"); click("저장")
        val parent = app.library.snapshot().folders.single { it.name == "검증 프로젝트" }.also { ownedFolders += it.id }
        selectFolder(parent.name)
        manageFolder(parent.id); click("하위 폴더 만들기")
        setDialogText("회의 자료"); click("저장")
        val child = app.library.snapshot().folders.single { it.parentId == parent.id }.also { ownedFolders += it.id }
        assertEquals("회의 자료", child.name)
        selectFolder(child.name)
        manageFolder(child.id); click("이름 변경"); setDialogText("설계 회의"); click("저장")
        assertEquals("설계 회의", LibraryStore(File(app.filesDir, "library.json")).snapshot().folders.single { it.id == child.id }.name)
        selectFolder("전체 녹음")
        click(first.title); click("녹음 더보기"); click("폴더 이동"); click("${parent.name} / 설계 회의")
        assertEquals(child.id, app.library.folderFor(first.id))
        back()
        click(second.title); click("녹음 더보기"); click("폴더 이동"); click(parent.name); back()
        selectFolder(parent.name)
        assertText(second.title); assertNoText(first.title); assertNoText(third.title)
        selectFolder("설계 회의")
        assertText(first.title); assertNoText(second.title)
        recreate()
        assertText(first.title); assertNoText(second.title)
        onMain { activity!!.finish() }; launch()
        selectFolder("설계 회의")
        assertText(first.title)
        assertEquals(child.id, LibraryStore(File(app.filesDir, "library.json")).folderFor(first.id))
        screenshot("folders-nested")
        selectFolder("전체 녹음")
        screenshot("library-folders-populated")
        selectFolder(parent.name)
        manageFolder(parent.id); click("폴더 삭제"); click("취소")
        assertTrue(app.library.snapshot().folders.any { it.id == parent.id })
        manageFolder(parent.id); click("폴더 삭제"); click("삭제")
        val reopened = LibraryStore(File(app.filesDir, "library.json"))
        assertFalse(reopened.snapshot().folders.any { it.id == parent.id || it.id == child.id })
        assertNull(reopened.folderFor(first.id)); assertNull(reopened.folderFor(second.id))
        selectFolder("미분류")
        for (record in listOf(first, second, third)) {
            assertText(record.title)
            val bytes = before.getValue(record.id)
            assertTrue(bytes.first.contentEquals(app.store.audio(record.id).readBytes()))
            assertTrue(bytes.second.contentEquals(File(app.store.audio(record.id).parentFile, "local.json").readBytes()))
            assertTrue(bytes.third.contentEquals(Wire.marker(app.store.get(record.id))))
            assertEquals(record, app.store.get(record.id))
        }
    }

    fun testSearchFocusDetailTabsBackRecreationAndLibraryScreenshots() {
        val meeting = fixture("주간 기획 회의", withResult = true)
        val design = fixture("서비스 디자인 검토")
        fixture("다음 주 아이디어 메모")
        val displayFolder = app.library.createFolder("화면 검증 폴더").also { ownedFolders += it.id }
        app.library.assign(design.id, displayFolder.id)
        launch()
        assertLibrary()
        screenshot("library-populated")
        var field: EditText? = null
        onMain {
            field = searchField()
            field!!.requestFocus()
            for (character in "기획") field!!.append(character.toString())
            field!!.setSelection(1)
        }
        assertText(meeting.title); assertNoText(design.title)
        app.changed()
        instrumentation.waitForIdleSync()
        onMain {
            assertSame("Search input rebuilt during update", field, searchField())
            assertTrue(field!!.hasFocus()); assertEquals(1, field!!.selectionStart)
            field!!.text.insert(1, "간")
        }
        assertText("검색 결과가 없습니다.")
        onMain { field!!.setText("기획"); field!!.setSelection(1) }
        recreate()
        onMain {
            assertEquals("기획", searchField().text.toString())
            assertTrue(searchField().hasFocus()); assertEquals(1, searchField().selectionStart)
            searchField().setText(""); searchField().clearFocus()
        }
        click(meeting.title)
        val transcript = "오늘 회의에서는 녹음 보관과 폴더 정리 방법을 논의했습니다."
        val notes = "회의 요약\n\n다음 회의 전까지 설계안을 검토하고 의견을 정리합니다."
        assertText(transcript); assertNoText(notes)
        screenshot("detail-transcript")
        click("노트"); assertText(notes); assertNoText(transcript)
        recreate(); assertText(notes)
        screenshot("detail-notes")
        back(); assertLibrary(); assertText(design.title)
        click("설정"); assertText("기본 모드"); back(); assertLibrary()
        click("새 녹음"); assertText("녹음 시작"); back(); assertLibrary()
        captureDisplayVariants()
    }

    fun testActualCaptureNavigationFolderAssignmentPlaybackAndStaleFolder() {
        shell("pm grant com.vemo.android android.permission.RECORD_AUDIO")
        if (android.os.Build.VERSION.SDK_INT >= 33) shell("pm grant com.vemo.android android.permission.POST_NOTIFICATIONS")
        val oldMode = app.preferences.getString("mode", null)
        val oldExport = app.preferences.getString("folder", null)
        app.preferences.edit().putString("mode", "record_only").remove("folder").commit()
        try {
            val folder = app.library.createFolder("검증 녹음").also { ownedFolders += it.id }
            launch(); selectFolder(folder.name)
            val existingIds = app.store.list().map { it.id }.toSet()
            click("새 녹음"); click("녹음 시작")
            await("No new capture") {
                app.store.list().firstOrNull { it.id !in existingIds }?.let {
                    captureId = it.id; ownedRecordings += it.id; true
                } ?: false
            }
            val id = captureId!!
            awaitCapture(CaptureState.RECORDING)
            assertEquals(folder.id, app.library.folderFor(id))
            screenshot("recording-active")
            back(); assertLibrary()
            openActiveCapture(); assertText("일시정지")
            back(); assertLibrary()
            click("설정"); assertText("기본 모드"); back(); assertLibrary()
            assertEquals("Navigation must preserve the owned active capture", CaptureState.RECORDING, app.store.get(id).state)
            openActiveCapture(); recreate(); assertText("일시정지")
            click("일시정지"); awaitCapture(CaptureState.PAUSED)
            assertFalse(app.store.get(id).uploadable)
            back(); openActiveCapture(); click("재개"); awaitCapture(CaptureState.RECORDING)
            SystemClock.sleep(3000)
            click("완료")
            val complete = awaitCapture(CaptureState.COMPLETE)
            await("Capture gate not released") { !app.gate.isRecording() }
            assertTrue(complete.bytes > 0); assertTrue(complete.durationMs > 0)
            assertEquals(complete.hash, sha256(app.store.audio(id)))
            assertEquals(folder.id, LibraryStore(File(app.filesDir, "library.json")).folderFor(id))
            back(); click(complete.title)
            screenshot("detail-real-capture")
            click("녹음 듣기"); assertText("재생 중지")
            click("재생 중지"); assertText("녹음 듣기")
            assertEquals(complete.hash, sha256(app.store.audio(id)))
            back()
            val stale = app.library.createFolder("검증 임시 폴더").also { ownedFolders += it.id }
            app.library.deleteFolder(stale.id)
            val beforeStale = app.store.list().map { it.id }.toSet()
            command("START", stale.id)
            await("Stale folder prevented capture") {
                app.store.list().firstOrNull { it.id !in beforeStale }?.let {
                    captureId = it.id; ownedRecordings += it.id; true
                } ?: false
            }
            awaitCapture(CaptureState.RECORDING)
            assertNull(app.library.folderFor(captureId!!))
            SystemClock.sleep(2000)
            command("STOP")
            val staleComplete = awaitCapture(CaptureState.COMPLETE)
            assertTrue(staleComplete.bytes > 0)
            assertEquals(staleComplete.hash, sha256(app.store.audio(staleComplete.id)))
        } finally {
            val edit = app.preferences.edit()
            if (oldMode == null) edit.remove("mode") else edit.putString("mode", oldMode)
            if (oldExport == null) edit.remove("folder") else edit.putString("folder", oldExport)
            edit.commit()
        }
    }

    fun testApi36NativeBackGestureReturnsDetailAndSettingsToLibrary() {
        if (android.os.Build.VERSION.SDK_INT < 33) return
        val oldOverlay = shell("cmd overlay list").lineSequence().firstOrNull {
            it.startsWith("[x]") && it.contains("com.android.internal.systemui.navbar.")
        }?.substringAfter("] ")?.trim()
        val record = fixture("탐색 동작 검증")
        try {
            shell("cmd overlay enable-exclusive --category com.android.internal.systemui.navbar.gestural")
            await("Gesture navigation did not activate") { shell("settings get secure navigation_mode") == "2" }
            launch()
            click(record.title); assertText("녹음 듣기")
            gestureBack(); await("Native Back did not return detail to library") { hasSearchField() }
            click("설정"); assertText("기본 모드")
            gestureBack(); await("Native Back did not return settings to library") { hasSearchField() }
            assertFalse("Native Back closed the activity", activity!!.isFinishing)
        } finally {
            if (oldOverlay != null) shell("cmd overlay enable-exclusive --category $oldOverlay")
        }
    }

    private fun hasSearchField(): Boolean {
        var found = false
        onMain { found = currentViews().any { it is EditText && it.contentDescription == "녹음 검색" } }
        return found
    }

    private fun gestureBack() {
        // Actual touch gesture exercises the platform OnBackInvokedDispatcher, unlike KEYCODE_BACK.
        SystemClock.sleep(500)
        val metrics = app.resources.displayMetrics
        val downTime = SystemClock.uptimeMillis()
        fun inject(action: Int, x: Float, time: Long) {
            val event = MotionEvent.obtain(downTime, time, action, x, metrics.heightPixels * .5f, 0)
            event.source = InputDevice.SOURCE_TOUCHSCREEN
            try { assertTrue("Gesture injection failed", instrumentation.uiAutomation.injectInputEvent(event, true)) }
            finally { event.recycle() }
        }
        inject(MotionEvent.ACTION_DOWN, 1f, downTime)
        repeat(20) { step ->
            SystemClock.sleep(20)
            inject(MotionEvent.ACTION_MOVE, 1f + metrics.widthPixels * .65f * (step + 1) / 20f, SystemClock.uptimeMillis())
        }
        inject(MotionEvent.ACTION_UP, metrics.widthPixels * .65f, SystemClock.uptimeMillis())
        instrumentation.waitForIdleSync()
    }

    private fun searchField(): EditText = currentViews().filterIsInstance<EditText>().single { it.contentDescription == "녹음 검색" }
    private fun assertLibrary() {
        onMain { assertTrue(searchField().isShown) }
    }
    private fun selectFolder(name: String) {
        var label: String? = null
        onMain { label = currentViews().firstOrNull { it.isClickable && it.contentDescription?.toString()?.substringBefore(",")?.let { path -> path == name || path.endsWith(" / $name") || path.endsWith("/$name") } == true }?.contentDescription?.toString() }
        click(label ?: error("Missing folder filter: $name"))
    }
    private fun manageFolder(id: String) {
        val path = app.library.path(id).replace("/", " / ")
        onMain {
            val view = currentViews().single { it.isLongClickable && it.contentDescription?.toString()?.substringBefore(",") == path }
            assertTrue("Folder context action unavailable", view.performLongClick())
        }
        instrumentation.waitForIdleSync()
    }
    private fun openActiveCapture() {
        var label: String? = null
        onMain { label = currentViews().filterIsInstance<TextView>().firstOrNull { it.isClickable && it.text.contains("열기") && (it.text.contains("녹음 중") || it.text.contains("일시정지")) }?.text?.toString() }
        click(label ?: error("Active capture entry is missing"))
    }

    private fun launch() {
        activity = instrumentation.startActivitySync(
            Intent(instrumentation.targetContext, MainActivity::class.java)
                .setAction(Intent.ACTION_MAIN).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK)
        )
        instrumentation.waitForIdleSync()
    }

    private fun onMain(block: () -> Unit) = instrumentation.runOnMainSync(block)
    private fun views(root: View): List<View> = listOf(root) +
        if (root is ViewGroup) (0 until root.childCount).flatMap { views(root.getChildAt(it)) } else emptyList()

    private fun currentViews(): List<View> = views(activity!!.window.decorView)
    private fun visibleText(value: String): Boolean {
        var found = false
        onMain { found = currentViews().any { it is TextView && it.isShown && it.text.toString() == value } }
        return found
    }

    private fun await(message: String, timeoutMs: Long = 10_000, condition: () -> Boolean) {
        val deadline = SystemClock.uptimeMillis() + timeoutMs
        while (SystemClock.uptimeMillis() < deadline) {
            instrumentation.waitForIdleSync()
            if (condition()) return
            SystemClock.sleep(50)
        }
        fail(message)
    }

    private fun assertText(value: String) = await("Expected visible text: $value") { visibleText(value) }
    private fun assertNoText(value: String) { instrumentation.waitForIdleSync(); assertFalse("Unexpected text: $value", visibleText(value)) }

    private fun click(value: String) {
        var clicked = false
        if (!clicked) {
            val root = instrumentation.uiAutomation.rootInActiveWindow
            try {
                val nodes = root?.findAccessibilityNodeInfosByText(value).orEmpty()
                try {
                    for (node in nodes) {
                        if (node.text?.toString() != value && node.contentDescription?.toString() != value) continue
                        var target: AccessibilityNodeInfo? = AccessibilityNodeInfo.obtain(node)
                        try {
                            while (target != null && !target.isClickable) {
                                val parent = target.parent
                                target.recycle(); target = parent
                            }
                            if (target?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true) { clicked = true; break }
                        } finally { target?.recycle() }
                    }
                } finally { nodes.forEach { it.recycle() } }
            } finally { root?.recycle() }
        }
        if (!clicked) {
            onMain {
                val view = currentViews().firstOrNull {
                it.isShown && (it.contentDescription?.toString() == value || it is TextView && it.text.toString() == value)
            }
                var target = view
                while (target != null && !target.isClickable) target = target.parent as? View
                if (target != null && target.isEnabled) clicked = target.performClick()
            }
        }
        assertTrue("No enabled control for: $value", clicked)
        instrumentation.waitForIdleSync()
    }

    private fun setDialogText(value: String) {
        val root = instrumentation.uiAutomation.rootInActiveWindow ?: error("No dialog window")
        fun edit(node: AccessibilityNodeInfo): Boolean {
            if (node.isEditable) return node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,
                Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, value) })
            for (index in 0 until node.childCount) {
                val child = node.getChild(index) ?: continue
                try { if (edit(child)) return true } finally { child.recycle() }
            }
            return false
        }
        try { assertTrue("Dialog has no editable name", edit(root)) } finally { root.recycle() }
        instrumentation.waitForIdleSync()
    }

    private fun command(action: String, folderId: String? = null) {
        val intent = Intent(app, RecordingService::class.java).setAction("com.vemo.$action")
        if (folderId != null) intent.putExtra("com.vemo.FOLDER_ID", folderId)
        if (action == "START") app.startForegroundService(intent) else app.startService(intent)
        instrumentation.waitForIdleSync()
    }

    private fun awaitCapture(state: CaptureState, id: String? = captureId): Recording {
        var result: Recording? = null
        await("Capture $id did not reach $state", 15_000) {
            result = app.store.list().firstOrNull { it.id == id && it.state == state }
            result != null
        }
        return result!!
    }

    private fun back() { instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK); instrumentation.waitForIdleSync() }
    private fun recreate() {
        val previous = activity!!
        onMain { previous.recreate() }
        await("Activity did not recreate") { activity !== previous && activity?.isDestroyed == false }
    }

    private fun screenshot(name: String) {
        SystemClock.sleep(4500) // Let transient notices, animations, and scrollbars settle.
        instrumentation.waitForIdleSync()
        val bitmap = instrumentation.uiAutomation.takeScreenshot() ?: error("Screenshot unavailable: $name")
        val directory = File(app.getExternalFilesDir(null), "library-ui-validation").apply { mkdirs() }
        try {
            File(directory, "$name.png").outputStream().use {
                assertTrue("Screenshot encoding failed", bitmap.compress(Bitmap.CompressFormat.PNG, 100, it))
            }
        } finally { bitmap.recycle() }
        instrumentation.sendStatus(0, Bundle().apply { putString("screenshot", File(directory, "$name.png").absolutePath) })
    }

    private fun shell(command: String): String = instrumentation.uiAutomation.executeShellCommand(command).use {
        ParcelFileDescriptor.AutoCloseInputStream(it).bufferedReader().use { reader -> reader.readText().trim() }
    }

    private fun captureDisplayVariants() {
        val oldScale = shell("settings get system font_scale")
        val oldOrientation = activity!!.requestedOrientation
        val densityOutput = shell("wm density")
        val oldDensity = densityOutput.lineSequence().firstOrNull { it.startsWith("Override density:") }?.substringAfter(":")?.trim()
        val sizeOutput = shell("wm size")
        val widthPx = sizeOutput.lineSequence().last().substringAfter(":").trim().substringBefore("x").toInt()
        try {
            shell("wm density ${kotlin.math.round(widthPx * 160f / 360f).toInt()}")
            onMain { activity!!.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT }
            await("360dp portrait did not apply") {
                activity!!.resources.configuration.screenWidthDp in 359..361 && decorOrientation(false)
            }
            shell("settings put system font_scale 1.3")
            await("Font scale did not update") { activity!!.resources.configuration.fontScale >= 1.29f && decorOrientation(false) }
            onMain { assertEquals(360f, activity!!.resources.configuration.screenWidthDp.toFloat(), 1f) }
            screenshot("library-360dp-large-font")
            onMain { activity!!.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
            await("Landscape did not apply") { activity!!.resources.configuration.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE && decorOrientation(true) }
            SystemClock.sleep(4500)
            onMain {
                val recent = currentViews().filterIsInstance<TextView>().first { it.text.toString() == "다음 주 아이디어 메모" }
                val bounds = android.graphics.Rect()
                assertTrue("Landscape must initially expose a recording row", recent.getGlobalVisibleRect(bounds) && bounds.height() >= recent.height / 2)
                val folder = currentViews().filterIsInstance<TextView>().first { it.text.toString() == "화면 검증 폴더" }
                assertTrue("Landscape must initially expose a real folder", folder.getGlobalVisibleRect(bounds) && bounds.height() >= folder.height / 2)
            }
            screenshot("library-landscape-large-font")
        } finally {
            onMain { activity!!.requestedOrientation = oldOrientation }
            if (oldDensity == null) shell("wm density reset") else shell("wm density $oldDensity")
            if (oldScale == "null") shell("settings delete system font_scale")
            else shell("settings put system font_scale $oldScale")
            await("Font scale was not restored") {
                kotlin.math.abs(activity!!.resources.configuration.fontScale - (oldScale.toFloatOrNull() ?: 1f)) < .01f
            }
        }
    }

    private fun decorOrientation(landscape: Boolean): Boolean {
        var correct = false
        onMain {
            val decor = activity!!.window.decorView
            correct = decor.width > 0 && decor.height > 0 && (decor.width > decor.height) == landscape
        }
        return correct
    }

    private fun fixture(suffix: String, withResult: Boolean = false): Recording {
        val processor = if (withResult) UUID.randomUUID().toString() else null
        val record = app.store.create(suffix, if (withResult) Mode.DESKTOP else Mode.RECORD_ONLY, processor)
        ownedRecordings += record.id
        app.store.partial(record.id).writeText("Synthetic instrumentation fixture: $prefix-$suffix")
        app.store.transition(record.id, CaptureState.FINALIZING)
        val complete = app.store.finish(record.id, 1500)
        if (withResult) app.store.saveResult(complete, Json.bytes(linkedMapOf(
            "schema_version" to 1, "recording_id" to complete.id, "audio_sha256" to complete.hash,
            "processor_id" to processor, "title" to complete.title, "recorded_at" to complete.recordedAt,
            "transcript" to "오늘 회의에서는 녹음 보관과 폴더 정리 방법을 논의했습니다.",
            "notes_markdown" to "회의 요약\n\n다음 회의 전까지 설계안을 검토하고 의견을 정리합니다."
        )))
        return complete
    }
}
