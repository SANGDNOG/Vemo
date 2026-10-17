package com.vemo.android

import android.app.Activity
import android.app.Application
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.graphics.Rect
import android.os.Bundle
import android.os.SystemClock
import android.test.InstrumentationTestCase
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.ViewGroup
import android.view.accessibility.AccessibilityNodeInfo
import android.widget.EditText
import android.widget.ScrollView
import android.widget.TextView
import com.vemo.android.core.*
import java.io.File
import java.util.UUID

/** Native touch sequences enter Android's real local drag session; no synthetic DragEvents. */
@Suppress("DEPRECATION", "OVERRIDE_DEPRECATION")
class LibraryInteractionTest : InstrumentationTestCase() {
    private lateinit var app: VemoApp
    @Volatile private var activity: Activity? = null
    private val folderIds = linkedSetOf<String>()
    private val recordingIds = linkedSetOf<String>()
    private val suffix = UUID.randomUUID().toString().take(6)
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
        // The legacy runner can start its test thread before Application.onCreate finishes.
        instrumentation.waitForIdleSync()
        app = instrumentation.targetContext.applicationContext as VemoApp
        assertFalse("An existing recording or sync must not be interrupted", app.gate.busy())
        app.registerActivityLifecycleCallbacks(lifecycle)
    }

    override fun tearDown() {
        try {
            activity?.let { current -> main { current.finish() } }
            instrumentation.waitForIdleSync()
            for (id in recordingIds) {
                app.library.assign(id, null)
                assertTrue("Cannot remove owned fixture $id", File(app.filesDir, "recordings/$id").deleteRecursively())
            }
            for (id in folderIds.toList().asReversed()) {
                if (app.library.snapshot().folders.any { it.id == id }) app.library.deleteFolder(id)
            }
        } finally {
            app.unregisterActivityLifecycleCallbacks(lifecycle)
            super.tearDown()
        }
    }

    fun testTouchFolderAndRecordingDragPreservesDescendantsAndImmutableBytes() {
        val source = folder("가 이동 폴더")
        val child = folder("하위 회의", source.id)
        val destination = folder("나 보관 폴더")
        val nested = recording("하위 폴더 회의 기록")
        val loose = recording("분류할 아이디어 메모")
        app.library.assign(nested.id, child.id)
        val immutable = immutableBytes(nested, loose)
        launch()
        drag(folderView(source.id), folderView(destination.id), "drag-folder-highlight")
        await("Folder drop did not change the parent") { reopened().snapshot().folders.single { it.id == source.id }.parentId == destination.id }
        assertEquals(source.id, reopened().snapshot().folders.single { it.id == child.id }.parentId)
        assertEquals(child.id, reopened().folderFor(nested.id))
        assertImmutable(immutable)

        val beforeInvalid = File(app.filesDir, "library.json").readBytes()
        drag(folderView(child.id), folderView(destination.id), cancel = true)
        assertTrue("Cancelled drag changed library bytes", beforeInvalid.contentEquals(File(app.filesDir, "library.json").readBytes()))
        drag(folderView(source.id), folderView(child.id))
        dismissErrorIfPresent()
        assertTrue("Descendant drop changed library bytes", beforeInvalid.contentEquals(File(app.filesDir, "library.json").readBytes()))
        drag(folderView(source.id), folderView(source.id))
        dismissErrorIfPresent()
        assertTrue("Self drop changed library bytes", beforeInvalid.contentEquals(File(app.filesDir, "library.json").readBytes()))
        assertImmutable(immutable)

        // Keep portrait: the real drag must autoscroll from recording rows back to navigation.
        drag(recordingView(loose.title), folderView(destination.id), "drag-recording-highlight")
        await("Recording drop did not persist membership") { reopened().folderFor(loose.id) == destination.id }
        holdRelease(recordingView(loose.title)); clickDialog("폴더 이동"); clickDialog("미분류")
        await("Unfiled move did not clear membership") { reopened().folderFor(loose.id) == null }
        assertImmutable(immutable)
        recreate()
        assertEquals(destination.id, reopened().snapshot().folders.single { it.id == source.id }.parentId)
        assertEquals(child.id, reopened().folderFor(nested.id))
        assertNull(reopened().folderFor(loose.id))
    }

    fun testSingleRootInvalidOutsideDropReleasesInteractionAndRefresh() {
        assertTrue("This edge case requires the dedicated device's empty library", app.library.snapshot().folders.isEmpty())
        val source = folder("단일 최상위 폴더")
        val before = File(app.filesDir, "library.json").readBytes()
        launch()
        val point = bounds(folderView(source.id))
        val outside = bounds(findView { it is TextView && it.text.toString() == "Vemo" }, false)
        val down = SystemClock.uptimeMillis()
        inject(MotionEvent.ACTION_DOWN, point.exactCenterX(), point.exactCenterY(), down)
        SystemClock.sleep(ViewConfiguration.getLongPressTimeout().toLong() + 200)
        inject(MotionEvent.ACTION_MOVE, point.exactCenterX() + ViewConfiguration.get(app).scaledTouchSlop * 3f, point.exactCenterY(), down)
        SystemClock.sleep(150)
        repeat(16) { step ->
            inject(MotionEvent.ACTION_MOVE, outside.exactCenterX(), point.exactCenterY() + (outside.exactCenterY() - point.exactCenterY()) * (step + 1) / 16, down)
            SystemClock.sleep(20)
        }
        inject(MotionEvent.ACTION_UP, outside.exactCenterX(), outside.exactCenterY(), down)
        instrumentation.waitForIdleSync(); SystemClock.sleep(300)
        assertTrue("Outside drop changed library", before.contentEquals(File(app.filesDir, "library.json").readBytes()))
        tap(folderView(source.id))
        main { assertTrue("Tap after invalid drag stopped working", folderViewOnMain(source.id).isSelected) }
        holdRelease(folderView(source.id))
        await("Hold after invalid drag stopped working") { nodeText("하위 폴더 만들기") }
        clickDialog("취소")
        val added = folder("새로고침 확인")
        app.changed()
        await("Invalid drag left library refresh frozen") {
            var visible = false
            main { visible = allViews().any { it.contentDescription?.toString()?.startsWith("${added.name},") == true } }
            visible
        }
    }

    fun testRoundedMenusCreateRenameMoveDeleteCancelAndRotation() {
        val destination = folder("보관함")
        val memo = recording("메뉴 동작 검증 기록")
        val immutable = immutableBytes(memo)
        launch()
        val initial = File(app.filesDir, "library.json").readBytes()
        tap(findView { it is TextView && it.text.toString() == "＋ 폴더" })
        setDialogText("취소할 폴더 $suffix", focus = true)
        screenshot("rounded-create-input")
        clickDialog("취소")
        assertTrue(initial.contentEquals(File(app.filesDir, "library.json").readBytes()))
        tap(findView { it is TextView && it.text.toString() == "＋ 폴더" })
        setDialogText("새 프로젝트 $suffix"); clickDialog("저장")
        val created = reopened().snapshot().folders.single { it.name == "새 프로젝트 $suffix" }.also { folderIds += it.id }
        holdRelease(folderView(created.id)); clickDialog("하위 폴더 만들기")
        setDialogText("회의 자료 $suffix"); clickDialog("저장")
        val child = reopened().snapshot().folders.single { it.parentId == created.id }.also { folderIds += it.id }
        app.library.assign(memo.id, child.id)
        app.changed(); instrumentation.waitForIdleSync()
        holdRelease(folderView(created.id)); clickDialog("이름 변경")
        setDialogText("정리한 프로젝트 $suffix"); screenshot("rounded-rename-input"); clickDialog("저장")
        assertEquals("정리한 프로젝트 $suffix", reopened().snapshot().folders.single { it.id == created.id }.name)
        holdRelease(folderView(created.id)); clickDialog("폴더 이동")
        assertFalse("Move chooser offers self", nodeText("정리한 프로젝트 $suffix"))
        assertFalse("Move chooser offers descendant", nodeText("정리한 프로젝트 $suffix / 회의 자료 $suffix"))
        screenshot("rounded-folder-move")
        clickDialog("취소")
        assertNull(reopened().snapshot().folders.single { it.id == created.id }.parentId)
        holdRelease(folderView(created.id)); clickDialog("폴더 이동"); clickDialog(destination.name)
        assertEquals(destination.id, reopened().snapshot().folders.single { it.id == created.id }.parentId)
        assertEquals(child.id, reopened().folderFor(memo.id))
        assertImmutable(immutable)

        val beforeRotation = File(app.filesDir, "library.json").readBytes()
        holdRelease(folderView(created.id)); clickDialog("이름 변경"); setDialogText("저장하지 않은 이름")
        landscape() // Open edits are safely dismissed when the activity is rotated.
        assertTrue("Rotation committed an unsaved edit", beforeRotation.contentEquals(File(app.filesDir, "library.json").readBytes()))
        assertLibrary()
        assertFalse("Old dialog survived with stale activity", nodeText("저장하지 않은 이름"))
        holdRelease(recordingView(memo.title))
        await("Recording hold did not open context menu") { nodeText("열기") && nodeText("폴더 이동") }
        screenshot("rounded-recording-management")
        clickDialog("폴더 이동"); screenshot("rounded-recording-move"); clickDialog("취소")
        assertEquals(child.id, reopened().folderFor(memo.id))
        holdRelease(recordingView(memo.title)); clickDialog("열기")
        await("Recording context Open failed") { nodeText("녹음 재생") }
        back(); assertLibrary()
        holdRelease(folderView(created.id)); clickDialog("폴더 삭제")
        screenshot("rounded-delete-confirmation")
        clickDialog("취소")
        assertTrue(beforeRotation.contentEquals(File(app.filesDir, "library.json").readBytes()))
        holdRelease(folderView(created.id)); clickDialog("폴더 삭제"); clickDialog("삭제")
        assertFalse(reopened().snapshot().folders.any { it.id == created.id || it.id == child.id })
        assertNull(reopened().folderFor(memo.id))
        assertImmutable(immutable)
    }

    fun testOrdinaryScrollThenHoldReleaseAndAccessibleMenuDoNotClickOrMutate() {
        val destination = folder("가 00 보관")
        repeat(16) { folder("나 %02d 자료".format(it)) }
        val source = folder("하 99 이동")
        val before = File(app.filesDir, "library.json").readBytes()
        launch()
        val point = bounds(folderView(destination.id))
        val scroll = findView { it is ScrollView } as ScrollView
        val down = SystemClock.uptimeMillis()
        inject(MotionEvent.ACTION_DOWN, point.exactCenterX(), point.exactCenterY(), down)
        repeat(12) { step ->
            inject(MotionEvent.ACTION_MOVE, point.exactCenterX(), point.exactCenterY() - (step + 1) * 20f, down)
            SystemClock.sleep(12)
        }
        inject(MotionEvent.ACTION_UP, point.exactCenterX(), point.exactCenterY() - 240f, down)
        await("Ordinary touch movement was swallowed instead of scrolling") {
            var moved = false; main { moved = scroll.scrollY > 0 }; moved
        }
        assertFalse("Scroll accidentally opened management", nodeText("하위 폴더 만들기"))
        assertTrue("Scroll changed metadata", before.contentEquals(File(app.filesDir, "library.json").readBytes()))
        assertLibrary()
        val sourceView = folderView(source.id)
        val held = bounds(sourceView)
        val heldAt = SystemClock.uptimeMillis()
        diagnostic("before hold", sourceView, held)
        inject(MotionEvent.ACTION_DOWN, held.exactCenterX(), held.exactCenterY(), heldAt)
        SystemClock.sleep(ViewConfiguration.getLongPressTimeout().toLong() + 200)
        assertFalse("Stationary hold opened menu before release", nodeText("하위 폴더 만들기"))
        inject(MotionEvent.ACTION_UP, held.exactCenterX(), held.exactCenterY(), heldAt)
        diagnostic("after hold", sourceView, held)
        if (!nodeText("하위 폴더 만들기")) screenshot("scroll-hold-failure", 0)
        await("Hold-release did not open folder menu") { nodeText("하위 폴더 만들기") }
        screenshot("rounded-folder-management")
        back()
        assertLibrary()
        main { assertTrue("Accessibility long click unavailable", folderViewOnMain(source.id).performLongClick()) }
        await("Accessible long click did not immediately open menu") { nodeText("하위 폴더 만들기") }
        back()
        assertTrue("Management-only interaction changed metadata", before.contentEquals(File(app.filesDir, "library.json").readBytes()))
        // Source is below the viewport; destination is at the beginning of the scroll content.
        drag(folderView(source.id), folderView(destination.id), "drag-autoscroll-highlight")
        await("Edge autoscroll/drop did not reparent folder") { reopened().snapshot().folders.single { it.id == source.id }.parentId == destination.id }
    }

    private fun diagnostic(stage: String, target: View, injected: Rect) {
        var info = ""
        main {
            val actual = Rect(); target.getGlobalVisibleRect(actual)
            info = "$stage injected=$injected actual=$actual attached=${target.isAttachedToWindow} pressed=${target.isPressed} selected=${target.isSelected} scrollY=${allViews().filterIsInstance<ScrollView>().first().scrollY}"
        }
        instrumentation.sendStatus(0, Bundle().apply { putString("diagnostic", info) })
    }

    private fun folderViewOnMain(id: String): View {
        val path = app.library.path(id).replace("/", " / ")
        return allViews().single { it.isLongClickable && it.contentDescription?.toString()?.substringBefore(",") == path }
    }
    private fun assertLibrary() {
        main { assertTrue("Library replaced by accidental item click", allViews().any { it is EditText && it.contentDescription == "녹음 검색" }) }
    }
    private fun back() {
        instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
        instrumentation.waitForIdleSync()
    }

    private fun folder(name: String, parent: String? = null): LibraryFolder =
        app.library.createFolder("$name $suffix", parent).also { folderIds += it.id }

    private fun recording(title: String): Recording {
        val item = app.store.create(title, Mode.RECORD_ONLY, null)
        recordingIds += item.id
        app.store.partial(item.id).writeText("Synthetic interaction fixture $suffix ${item.id}")
        app.store.transition(item.id, CaptureState.FINALIZING)
        return app.store.finish(item.id, 1800)
    }

    private fun reopened() = LibraryStore(File(app.filesDir, "library.json"))
    private fun immutableBytes(vararg records: Recording): Map<File, ByteArray> = records.flatMap {
        listOf(app.store.audio(it.id), File(app.store.audio(it.id).parentFile, "local.json"))
    }.associateWith { it.readBytes() }
    private fun assertImmutable(before: Map<File, ByteArray>) {
        for ((file, bytes) in before) assertTrue("Immutable fixture changed: ${file.name}", bytes.contentEquals(file.readBytes()))
    }

    private fun launch() {
        activity = instrumentation.startActivitySync(Intent(app, MainActivity::class.java)
            .setAction(Intent.ACTION_MAIN).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK))
        instrumentation.waitForIdleSync()
    }
    private fun main(block: () -> Unit) = instrumentation.runOnMainSync(block)
    private fun views(root: View): List<View> = listOf(root) +
        if (root is ViewGroup) (0 until root.childCount).flatMap { views(root.getChildAt(it)) } else emptyList()
    private fun allViews() = views(activity!!.window.decorView)
    private fun findView(predicate: (View) -> Boolean): View {
        var found: View? = null
        main { found = allViews().firstOrNull(predicate) }
        return found ?: error("Matching view not found")
    }
    private fun folderView(id: String): View = filterView(app.library.path(id).replace("/", " / "))
    private fun filterView(path: String): View = findView {
        it.isClickable && it.contentDescription?.toString()?.substringBefore(",") == path
    }
    private fun recordingView(title: String): View = findView {
        it.isClickable && it.contentDescription?.toString()?.startsWith("$title,") == true
    }
    private fun bounds(view: View, reveal: Boolean = true): Rect {
        if (reveal) {
            settleScroll()
            main { view.requestRectangleOnScreen(Rect(0, 0, view.width, view.height), true) }
            instrumentation.waitForIdleSync()
            settleScroll()
        }
        val rect = Rect()
        main { assertTrue("Touch target not visible", view.getGlobalVisibleRect(rect)); assertTrue(rect.width() > 0 && rect.height() > 0) }
        return rect
    }
    private fun settleScroll() {
        val deadline = SystemClock.uptimeMillis() + 5000
        var previous: Int? = null
        var stableSince = SystemClock.uptimeMillis()
        while (SystemClock.uptimeMillis() < deadline) {
            var current = 0
            main { current = allViews().filterIsInstance<ScrollView>().first().scrollY }
            if (current != previous) { previous = current; stableSince = SystemClock.uptimeMillis() }
            if (SystemClock.uptimeMillis() - stableSince >= 400) return
            SystemClock.sleep(60)
        }
        fail("Scroll did not settle before touch injection")
    }

    private fun inject(action: Int, x: Float, y: Float, down: Long) {
        val event = MotionEvent.obtain(down, SystemClock.uptimeMillis(), action, x, y, 0)
        event.source = InputDevice.SOURCE_TOUCHSCREEN
        try { assertTrue("Touch injection failed", instrumentation.uiAutomation.injectInputEvent(event, true)) }
        finally { event.recycle() }
    }
    private fun tap(view: View) {
        val point = bounds(view); val down = SystemClock.uptimeMillis()
        inject(MotionEvent.ACTION_DOWN, point.exactCenterX(), point.exactCenterY(), down)
        SystemClock.sleep(60)
        inject(MotionEvent.ACTION_UP, point.exactCenterX(), point.exactCenterY(), down)
        instrumentation.waitForIdleSync()
    }
    private fun holdRelease(view: View) {
        val point = bounds(view); val down = SystemClock.uptimeMillis()
        inject(MotionEvent.ACTION_DOWN, point.exactCenterX(), point.exactCenterY(), down)
        SystemClock.sleep(ViewConfiguration.getLongPressTimeout().toLong() + 200)
        inject(MotionEvent.ACTION_UP, point.exactCenterX(), point.exactCenterY(), down)
        instrumentation.waitForIdleSync()
    }
    private fun drag(source: View, target: View, screenshotName: String? = null, cancel: Boolean = false) {
        val from = bounds(source)
        val down = SystemClock.uptimeMillis()
        var x = from.exactCenterX(); var y = from.exactCenterY()
        inject(MotionEvent.ACTION_DOWN, x, y, down)
        try {
            SystemClock.sleep(ViewConfiguration.getLongPressTimeout().toLong() + 200)
            x += ViewConfiguration.get(app).scaledTouchSlop * 3f
            inject(MotionEvent.ACTION_MOVE, x, y, down)
            SystemClock.sleep(200)
            // Keep the pointer at the viewport edge until real app autoscroll reveals the target.
            val viewport = Rect()
            main { allViews().filterIsInstance<ScrollView>().first().getGlobalVisibleRect(viewport) }
            val deadline = SystemClock.uptimeMillis() + 12_000
            var to: Rect? = null
            while (SystemClock.uptimeMillis() < deadline) {
                main {
                    val candidate = Rect()
                    if (target.getGlobalVisibleRect(candidate) && candidate.height() >= target.height * .8 &&
                        candidate.centerY() in (viewport.top + 25)..(viewport.bottom - 25)) to = candidate
                }
                if (to != null) break
                val position = IntArray(2)
                main { target.getLocationOnScreen(position) }
                x = from.exactCenterX().coerceIn(viewport.left + 30f, viewport.right - 30f)
                y = if (position[1] < viewport.top) viewport.top + 20f else viewport.bottom - 20f
                inject(MotionEvent.ACTION_MOVE, x, y, down)
                SystemClock.sleep(80)
            }
            assertNotNull("Drag autoscroll did not expose target", to)
            // Leaving the edge stops autoscroll; follow the live target as remaining frames settle.
            x = viewport.exactCenterX(); y = viewport.exactCenterY()
            inject(MotionEvent.ACTION_MOVE, x, y, down)
            SystemClock.sleep(100)
            val startX = x; val startY = y
            repeat(24) { index ->
                val targetBounds = bounds(target, false)
                x = startX + (targetBounds.exactCenterX() - startX) * (index + 1) / 24f
                y = startY + (targetBounds.exactCenterY() - startY) * (index + 1) / 24f
                inject(MotionEvent.ACTION_MOVE, x, y, down)
                SystemClock.sleep(25)
            }
            SystemClock.sleep(100)
            if (screenshotName != null) screenshot(screenshotName, settleMs = 100)
            val finalBounds = bounds(target, false)
            x = finalBounds.exactCenterX(); y = finalBounds.exactCenterY()
            inject(MotionEvent.ACTION_MOVE, x, y, down)
            diagnostic("before drop", target, Rect(x.toInt(), y.toInt(), x.toInt() + 1, y.toInt() + 1))
            if (cancel) instrumentation.sendKeyDownUpSync(KeyEvent.KEYCODE_BACK)
        } finally { inject(MotionEvent.ACTION_UP, x, y, down) }
        instrumentation.waitForIdleSync()
        SystemClock.sleep(400)
    }
    private fun await(message: String, condition: () -> Boolean) {
        val deadline = SystemClock.uptimeMillis() + 10_000
        while (SystemClock.uptimeMillis() < deadline) {
            instrumentation.waitForIdleSync()
            if (condition()) return
            SystemClock.sleep(50)
        }
        fail(message)
    }
    private fun landscape() {
        main { activity!!.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
        await("Landscape layout not ready") {
            var ready = false
            main { ready = activity!!.window.decorView.let { it.width > it.height } }
            ready
        }
        SystemClock.sleep(600)
    }
    private fun recreate() {
        val old = activity!!
        main { old.recreate() }
        await("Activity did not recreate") { activity !== old && activity?.isDestroyed == false }
    }
    private fun screenshot(name: String, settleMs: Long = 1000) {
        SystemClock.sleep(settleMs)
        instrumentation.waitForIdleSync()
        val bitmap = instrumentation.uiAutomation.takeScreenshot() ?: error("Screenshot unavailable")
        val file = File(app.getExternalFilesDir(null), "library-interaction-validation/$name.png")
        file.parentFile!!.mkdirs()
        try { file.outputStream().use { assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)) } }
        finally { bitmap.recycle() }
        instrumentation.sendStatus(0, Bundle().apply { putString("screenshot", file.absolutePath) })
    }
    private fun nodeText(value: String): Boolean {
        val root = instrumentation.uiAutomation.rootInActiveWindow ?: return false
        try {
            val nodes = root.findAccessibilityNodeInfosByText(value)
            try { return nodes.any { it.text?.toString() == value || it.contentDescription?.toString() == value } }
            finally { nodes.forEach { it.recycle() } }
        } finally { root.recycle() }
    }
    private fun clickDialog(value: String) {
        var clicked = false
        val root = instrumentation.uiAutomation.rootInActiveWindow ?: error("No dialog window")
        try {
            val nodes = root.findAccessibilityNodeInfosByText(value)
            try {
                for (node in nodes) {
                    if (node.text?.toString() != value && node.contentDescription?.toString() != value) continue
                    var current: AccessibilityNodeInfo? = AccessibilityNodeInfo.obtain(node)
                    try {
                        while (current != null && !current.isClickable) {
                            val parent = current.parent; current.recycle(); current = parent
                        }
                        if (current?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true) { clicked = true; break }
                    } finally { current?.recycle() }
                }
            } finally { nodes.forEach { it.recycle() } }
        } finally { root.recycle() }
        assertTrue("Dialog action unavailable: $value", clicked)
        instrumentation.waitForIdleSync()
    }
    private fun setDialogText(value: String, focus: Boolean = false) {
        fun edit(node: AccessibilityNodeInfo): Boolean {
            if (node.isEditable) {
                if (focus) node.performAction(AccessibilityNodeInfo.ACTION_CLICK)
                return node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,
                    Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, value) })
            }
            for (index in 0 until node.childCount) {
                val child = node.getChild(index) ?: continue
                try { if (edit(child)) return true } finally { child.recycle() }
            }
            return false
        }
        val root = instrumentation.uiAutomation.rootInActiveWindow ?: error("No dialog")
        try { assertTrue("No dialog input", edit(root)) } finally { root.recycle() }
        instrumentation.waitForIdleSync()
    }
    private fun dismissErrorIfPresent() { if (nodeText("확인")) clickDialog("확인") }
}
