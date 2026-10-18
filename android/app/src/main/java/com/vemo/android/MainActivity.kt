package com.vemo.android

import android.Manifest
import android.app.*
import android.content.*
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.MediaPlayer
import android.net.Uri
import android.os.*
import android.text.InputType
import android.text.Editable
import android.text.TextWatcher
import android.view.*
import android.widget.*
import com.vemo.android.core.*
import com.google.android.gms.auth.api.identity.Identity
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter

/** Small native Android UI. No mobile inference controls until that runtime exists. */
class MainActivity : Activity() {
    private val purple by lazy { getColor(R.color.vemo_design_accent) }
    private val deleteRed by lazy { getColor(R.color.vemo_design_destructive) }
    private val ink by lazy { getColor(R.color.vemo_design_text) }
    private val muted by lazy { getColor(R.color.vemo_design_secondary) }
    private val background by lazy { getColor(R.color.vemo_design_surface) }
    private val subtle by lazy { getColor(R.color.vemo_design_sidebar) }
    private val selection by lazy { getColor(R.color.vemo_design_selection) }
    private val line by lazy { getColor(R.color.vemo_design_divider) }
    private val ripple by lazy { android.content.res.ColorStateList.valueOf(getColor(R.color.vemo_design_ripple)) }
    private lateinit var container: LinearLayout
    private lateinit var content: LinearLayout
    private var tab = "list"
    private var selectedFolder: String? = null
    private var unfiled = false
    private var query = ""
    private var detailTab = "transcript"
    private var entries: LinearLayout? = null
    private var search: EditText? = null
    private var recordingAction: Button? = null
    private var syncAction: ImageButton? = null
    private var libraryError: String? = null
    private var interactionFrozen = false
    private var refreshPending = false
    private var mainScroll: LibraryDragScroll? = null
    private val holds = mutableListOf<LibraryHold>()
    private var managementDialog: Dialog? = null
    private var lastStructure = ""
    private var lastNotice = ""
    private var backCallback: android.window.OnBackInvokedCallback? = null
    private data class FolderControl(val view: LinearLayout, val name: TextView, val count: TextView, val folderId: String?, val unfiled: Boolean)
    private val folderControls = mutableListOf<FolderControl>()
    private val folderActionViews = mutableMapOf<String, View>()
    private var folderFocusKey: String? = null
    private var folderAccessibilityFocus = false
    private var focusRenderGeneration = 0
    private fun rememberFolderFocus(key: String) {
        folderFocusKey = key
        folderAccessibilityFocus = folderActionViews[key]?.isAccessibilityFocused == true
    }
    private fun restoreActionFocus() {
        val origin = folderFocusKey ?: return
        if (!activityResumed || isFinishing || isDestroyed || managementDialog != null) return
        val detailOrigin = origin.startsWith("detail-more:") || origin.startsWith("breadcrumb:")
        val sameScreen = if (detailOrigin) origin.substringAfter(':') == detailId && tab == "list"
            else tab == "list" && detailId == null
        if (!sameScreen) { folderFocusKey = null; folderAccessibilityFocus = false; return }
        val target = folderActionViews[origin] ?: if (detailOrigin) folderActionViews["detail-more:$detailId"]
            else folderActionViews["create-folder"] ?: search
        if (target == null) return
        val generation = focusRenderGeneration
        val root = container
        val accessibility = folderAccessibilityFocus
        target.post {
            if (activityResumed && !isFinishing && !isDestroyed && managementDialog == null &&
                focusRenderGeneration == generation && container === root && folderFocusKey == origin &&
                target.isAttachedToWindow && (folderActionViews[origin] === target ||
                    folderActionViews["create-folder"] === target || folderActionViews["detail-more:$detailId"] === target || search === target)) {
                target.requestFocus()
                if (accessibility) target.performAccessibilityAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_ACCESSIBILITY_FOCUS, null)
                folderFocusKey = null; folderAccessibilityFocus = false
            }
        }
    }
    private var detailId: String? = null
    private var amplitudeView: RecordingMeter? = null
    private var timer: TextView? = null
    private var statusView: TextView? = null
    private var refreshSettings: (() -> Unit)? = null
    private var activityResumed = false
    private var player: MediaPlayer? = null
    private var playerId: String? = null
    private var playbackSeek: SeekBar? = null
    private var playbackTime: TextView? = null
    private var playbackToggle: ImageButton? = null
    private var playbackRefreshPending = false
    private var playbackDragging = false
    private var playbackComplete = false
    private var pendingSeek: Int? = null
    private var seekInFlight: Int? = null
    private var revealPlayer = false
    private val playbackTick = object : Runnable {
        override fun run() {
            if (!activityResumed || player == null || playbackSeek == null) return
            updatePlaybackControls()
            if (activityResumed && player != null) handler.postDelayed(this, 250)
        }
    }
    private val handler = Handler(Looper.getMainLooper())
    private val listener: () -> Unit = {
        statusView?.text = vemo().message
        if (vemo().message != lastNotice) {
            lastNotice = vemo().message
            if (lastNotice.isNotBlank()) Toast.makeText(this, lastNotice, Toast.LENGTH_LONG).show()
        }
        // Keep the search field and keyboard intact when background sync changes rows.
        if (playbackDragging) { playbackRefreshPending = true }
        else if (interactionFrozen) { refreshPending = true }
        else if (tab == "list" && detailId == null && libraryStructure() == lastStructure) {
            updateEntries(); updateRecordingAction(); updateSyncAction()
        } else if (tab == "settings") refreshSettings?.invoke()
        else render()
    }
    private val tick = object : Runnable {
        override fun run() {
            timer?.text = duration(if (activeRecording() == null) 0L else vemo().elapsedMs)
            updateRecordingAction()
            handler.postDelayed(this, 1000)
        }
    }
    private var lastTimelinePoll = -300_000L
    private val poll = object : Runnable {
        override fun run() {
            if (!vemo().gate.busy() && vemo().cloudConfigured()) {
                val results = vemo().store.list().filter { it.mode == Mode.DESKTOP && it.uploaded }
                    .map { try { vemo().store.result(it) } catch (_: Exception) { null } }
                val now = SystemClock.elapsedRealtime()
                val waitingForResult = results.any { it == null }
                val waitingForTimeline = results.any { it != null && it.needsTimelineEnrichment }
                if (waitingForResult || (waitingForTimeline && now - lastTimelinePoll >= 300_000L)) {
                    lastTimelinePoll = now
                    vemo().sync()
                }
            }
            handler.postDelayed(this, 30_000)
        }
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        lastNotice = savedInstanceState?.getString("lastNotice") ?: vemo().message
        tab = savedInstanceState?.getString("tab") ?: "list"
        detailId = savedInstanceState?.getString("detail")
        selectedFolder = savedInstanceState?.getString("folder")
        unfiled = savedInstanceState?.getBoolean("unfiled") ?: false
        query = savedInstanceState?.getString("query") ?: ""
        detailTab = savedInstanceState?.getString("detailTab") ?: "transcript"
        window.statusBarColor = background
        window.navigationBarColor = background
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR
        window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        if (Build.VERSION.SDK_INT >= 33) {
            val callback = android.window.OnBackInvokedCallback { if (!navigateBack()) finish() }
            backCallback = callback
            onBackInvokedDispatcher.registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, callback)
        }
        render()
        if (savedInstanceState?.getBoolean("searchFocused") == true) search?.apply {
            requestFocus(); setSelection((savedInstanceState.getInt("searchCursor")).coerceIn(0, length()))
        }
    }
    override fun onSaveInstanceState(outState: Bundle) { super.onSaveInstanceState(outState); outState.putString("tab", tab); outState.putString("detail", detailId)
        outState.putString("folder", selectedFolder); outState.putBoolean("unfiled", unfiled)
        outState.putString("query", query); outState.putString("detailTab", detailTab)
        outState.putString("lastNotice", lastNotice)
        outState.putBoolean("searchFocused", search?.hasFocus() == true)
        outState.putInt("searchCursor", search?.selectionStart ?: query.length)
    }
    override fun onResume() { super.onResume(); activityResumed = true; vemo().listeners.add(listener); handler.post(tick); handler.postDelayed(poll, 30_000); SyncJobService.schedule(this); if (!::container.isInitialized) render() else listener(); amplitudeView?.updateSampling(); restoreActionFocus() }
    override fun onPause() { activityResumed = false; amplitudeView?.updateSampling(); if (interactionFrozen) container.cancelDragAndDrop(); holds.forEach { it.cancel() }; vemo().listeners.remove(listener); handler.removeCallbacks(tick); handler.removeCallbacks(poll); stopPlayer(); super.onPause() }
    override fun onDestroy() {
        if (Build.VERSION.SDK_INT >= 33) backCallback?.let { onBackInvokedDispatcher.unregisterOnBackInvokedCallback(it) }
        backCallback = null
        managementDialog?.dismiss(); managementDialog = null
        stopPlayer(); handler.removeCallbacksAndMessages(null); super.onDestroy()
    }
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
    private fun text(value: String, size: Float = 16f, color: Int = ink, bold: Boolean = false): TextView = TextView(this).apply {
        text = value; textSize = size; setTextColor(color); setPadding(0, dp(5), 0, dp(5)); if (bold) setTypeface(null, Typeface.BOLD)
    }
    private fun column(): LinearLayout = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
    private fun row(): LinearLayout = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
    private fun surface(color: Int, border: Boolean = false) = GradientDrawable().apply {
        setColor(color); cornerRadius = resources.getDimension(R.dimen.vemo_design_button_radius)
        if (border) setStroke(dp(1), line)
    }
    private fun button(label: String, primary: Boolean = false, action: () -> Unit): Button = Button(this).apply {
        text = label; isAllCaps = false; textSize = 15f; minHeight = dp(52)
        minimumWidth = 0; minWidth = 0
        stateListAnimator = null; elevation = 0f
        setPadding(dp(16), dp(12), dp(16), dp(12))
        setTextColor(if (primary) Color.WHITE else purple)
        backgroundTintList = null
        background = android.graphics.drawable.RippleDrawable(
            ripple,
            surface(if (primary) purple else subtle), null)
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(4); bottomMargin = dp(4) }
        setOnClickListener { action() }
    }
    private fun gap(height: Int = 12) { content.addView(Space(this), LinearLayout.LayoutParams(1, dp(height))) }
    private fun label(value: String, size: Float = 28f) { content.addView(text(value, size, ink, true)) }
    private fun card(): LinearLayout = column().apply {
        setPadding(dp(18), dp(20), dp(18), dp(20))
        background = surface(subtle)
    }
    private fun divider(target: LinearLayout) {
        target.addView(View(this).apply { setBackgroundColor(line) }, LinearLayout.LayoutParams(-1, dp(1)))
    }
    private fun home() { stopPlayer(); detailId = null; tab = "list"; render() }
    // API 33+ gestures use the native OnBackInvokedDispatcher registered in onCreate.
    // Keep this override for API 26–32 and legacy key dispatch; both call navigateBack().
    @android.annotation.SuppressLint("GestureBackNavigation")
    @Deprecated("Platform back navigation")
    override fun onBackPressed() {
        if (!navigateBack()) super.onBackPressed()
    }
    private fun navigateBack(): Boolean {
        if (interactionFrozen) { container.cancelDragAndDrop(); holds.forEach { it.cancel() }; return true }
        when {
            tab != "list" || detailId != null -> home()
            selectedFolder != null || unfiled -> { selectedFolder = null; unfiled = false; render() }
            query.isNotEmpty() -> { query = ""; search?.setText("") }
            else -> return false
        }
        return true
    }
    private fun activeRecording() = vemo().store.list().firstOrNull {
        it.state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)
    }
    private fun library(): LibraryState? = try {
        vemo().library.snapshot().also { libraryError = null }
    } catch (error: Exception) {
        libraryError = "폴더 정보를 읽을 수 없습니다. 원본 녹음은 아래 전체 목록에서 열 수 있습니다. " + (error.message ?: "")
        null
    }
    private fun libraryStructure(): String = library()?.folders.toString() + libraryError
    private fun folderTitle(): String = when {
        unfiled -> "미분류"
        selectedFolder != null -> library()?.folders?.firstOrNull { it.id == selectedFolder }?.name ?: "전체 녹음"
        else -> "전체 녹음"
    }
    private fun updateRecordingAction() {
        val active = activeRecording()
        recordingAction?.text = when (active?.state) {
            CaptureState.RECORDING -> "녹음 중 · ${duration(vemo().elapsedMs)} · 열기"
            CaptureState.PAUSED -> "Ⅱ 일시정지 · ${duration(vemo().elapsedMs)} · 열기"
            CaptureState.FINALIZING -> "녹음 저장 중 · 열기"
            else -> "새 녹음"
        }
    }
    private fun updateSyncAction() {
        syncAction?.apply {
            contentDescription = if (vemo().syncing) "동기화 중" else "수동 동기화"
            tooltipText = contentDescription
            isEnabled = !vemo().gate.busy()
            alpha = if (isEnabled) 1f else 0.4f
        }
    }
    private fun render() {
        if (interactionFrozen) { refreshPending = true; return }
        if (folderFocusKey == null) folderActionViews.entries.firstOrNull { it.value.hasFocus() || it.value.isAccessibilityFocused }?.let { rememberFolderFocus(it.key) }
        focusRenderGeneration++
        folderActionViews.clear()
        holds.forEach { it.cancel() }; holds.clear()
        handler.removeCallbacks(playbackTick)
        playbackSeek = null; playbackTime = null; playbackToggle = null; playbackDragging = false; playbackRefreshPending = false
        refreshSettings = null; amplitudeView = null; timer = null; search = null; entries = null; recordingAction = null; syncAction = null; folderControls.clear()
        val snapshot = library()
        if (selectedFolder != null && snapshot?.folders?.none { it.id == selectedFolder } == true) selectedFolder = null
        lastStructure = libraryStructure()
        container = column().apply {
            setBackgroundColor(this@MainActivity.background)
            isFocusableInTouchMode = true
            defaultFocusHighlightEnabled = false
        }
        container.setOnApplyWindowInsetsListener { view, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout() or WindowInsets.Type.ime())
                view.setPadding(bars.left + dp(20), bars.top + dp(8), bars.right + dp(20), bars.bottom + dp(8))
            } else view.setPadding(dp(20), dp(8), dp(20), dp(8))
            insets
        }
        val libraryHome = tab == "list" && detailId == null
        container.addView(row().apply {
            if (!libraryHome) addView(headerAction(R.drawable.ic_back, "라이브러리로 돌아가기") { home() })
            addView(text(if (libraryHome) folderTitle() else when (tab) {
                "settings" -> "설정"
                "record" -> "새 녹음"
                else -> "녹음"
            }, if (libraryHome) 28f else 25f, ink, true).apply {
                if (libraryHome) { maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END }
                else gravity = Gravity.CENTER
                contentDescription = text
            }, LinearLayout.LayoutParams(0, -2, 1f))
            if (libraryHome) {
                addView(ImageView(this@MainActivity).apply {
                    setImageResource(R.drawable.vemo_icon); importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
                }, LinearLayout.LayoutParams(dp(32), dp(32)).apply { marginStart = dp(8); marginEnd = dp(4) })
                addView(headerAction(R.drawable.ic_settings, "설정") { stopPlayer(); tab = "settings"; detailId = null; render() })
            } else if (detailId != null) {
                addView(headerAction(R.drawable.ic_more, "녹음 더보기") { detailId?.let { rememberFolderFocus("detail-more:$it"); detailActions(it) } }.apply {
                    folderActionViews["detail-more:$detailId"] = this
                })
            } else addView(Space(this@MainActivity), LinearLayout.LayoutParams(dp(48), dp(48)))
        })
        val scroll = LibraryDragScroll(this) { finishInteraction() }.apply { isFillViewport = true }
        mainScroll = scroll
        content = column().apply { val padding = if (tab == "list" && detailId == null && compactLibrary()) 4 else 16; setPadding(0, dp(padding), 0, dp(padding)) }
        scroll.addView(content)
        container.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        if (libraryHome) {
            divider(container)
            recordingAction = button("새 녹음") { stopPlayer(); detailId = null; tab = "record"; render() }.apply {
                setTextColor(ink)
                background = android.graphics.drawable.RippleDrawable(ripple, surface(Color.WHITE), null)
                setCompoundDrawablesRelativeWithIntrinsicBounds(R.drawable.ic_record_action, 0, 0, 0)
                compoundDrawablePadding = dp(10)
                gravity = Gravity.START or Gravity.CENTER_VERTICAL
                setPadding(0, dp(8), dp(12), dp(8))
            }
            container.addView(row().apply {
                addView(recordingAction, LinearLayout.LayoutParams(0, -2, 1f))
                if (activeRecording() == null && resources.configuration.fontScale <= 1.1f && resources.configuration.screenWidthDp >= 390)
                    addView(text("이 순간을 남겨요", 12f, muted))
            }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8); bottomMargin = dp(4) })
            updateRecordingAction()
        }
        setContentView(container)
        container.requestApplyInsets()
        statusView = null
        when (tab) { "settings" -> settings(); "list" -> if (detailId == null) recordings() else detail(detailId!!); else -> record() }
        restoreActionFocus()
    }
    private fun headerAction(icon: Int, title: String, action: () -> Unit) = ImageButton(this).apply {
        setImageResource(icon); imageTintList = android.content.res.ColorStateList.valueOf(muted)
        contentDescription = title; tooltipText = title
        background = android.graphics.drawable.RippleDrawable(ripple, surface(Color.WHITE), null)
        setPadding(dp(12), dp(12), dp(12), dp(12)); stateListAnimator = null; elevation = 0f
        layoutParams = LinearLayout.LayoutParams(dp(48), dp(48))
        setOnClickListener { action() }
    }
    private fun record() {
        val active = activeRecording()
        val paused = active?.state == CaptureState.PAUSED
        val finalizing = active?.state == CaptureState.FINALIZING
        content.gravity = Gravity.CENTER
        val stage = column().apply {
            gravity = Gravity.CENTER
            setPadding(0, dp(32), 0, dp(32))
        }
        content.addView(stage, LinearLayout.LayoutParams(-1, -2))
        stage.addView(text(when {
            active == null -> "녹음 준비"
            paused -> "일시정지됨"
            finalizing -> "녹음 저장 중"
            else -> "●  녹음 중"
        }, 14f, if (active != null && !paused) deleteRed else muted).apply { gravity = Gravity.CENTER })
        timer = text(if (active == null) "00:00" else duration(vemo().elapsedMs), 52f).apply {
            gravity = Gravity.CENTER
            typeface = Typeface.create("sans-serif-light", Typeface.NORMAL)
            setPadding(0, dp(22), 0, dp(8))
        }
        stage.addView(timer)
        stage.addView(text(active?.title ?: "새로운 기록", 14f, muted).apply { gravity = Gravity.CENTER })
        val folder = if (active == null) selectedFolder else library()?.recordingFolders?.get(active.id)
        val path = folder?.let { try { vemo().library.path(it) } catch (_: Exception) { "폴더 확인 필요" } } ?: "미분류"
        stage.addView(text("저장 위치 · $path", 13f, muted).apply { gravity = Gravity.CENTER })
        amplitudeView = RecordingMeter().apply {
            contentDescription = if (active == null) "녹음 시작 전 · 마이크 입력 없음" else if (paused || finalizing) "마이크 입력 표시 중지됨" else "실제 마이크 입력 레벨"
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_YES
        }
        stage.addView(amplitudeView, LinearLayout.LayoutParams(-1, dp(if (resources.configuration.screenHeightDp < 500) 64 else 140)).apply {
            topMargin = dp(20); bottomMargin = dp(28)
        })
        stage.addView(row().apply {
            gravity = Gravity.CENTER
            if (active == null) {
                addView(recordControl("녹음 시작", R.drawable.ic_mic, true, !vemo().gate.busy()) { startRecording() })
            } else {
                addView(recordControl(if (paused) "재개" else "일시정지", if (paused) R.drawable.ic_play else R.drawable.ic_pause, false, !finalizing) {
                    command(if (paused) "RESUME" else "PAUSE")
                }, LinearLayout.LayoutParams(0, -2, 1f))
                addView(recordControl("완료", R.drawable.ic_stop, true, !finalizing) { command("STOP") }, LinearLayout.LayoutParams(0, -2, 1f))
            }
        }, LinearLayout.LayoutParams(-1, -2))
        stage.addView(text(if (finalizing) "원본을 확정하고 있습니다. 잠시 기다려 주세요." else if (active == null) "준비되면 녹음을 시작하세요." else "화면을 나가도 녹음은 계속됩니다.", 13f, muted).apply {
            gravity = Gravity.CENTER; setPadding(0, dp(28), 0, 0)
        })
        if (vemo().store.unreadableCount() > 0) content.addView(text("읽을 수 없는 로컬 메타데이터가 있습니다. 앱을 삭제하지 마세요. 원본 폴더는 보존했습니다.", 13f, deleteRed))
    }
    private inner class RecordingMeter : View(this@MainActivity) {
        private var meterAttached = false
        private val levels = java.util.ArrayDeque<Int>()
        private val paint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
            color = deleteRed; strokeWidth = dp(3).toFloat(); strokeCap = android.graphics.Paint.Cap.ROUND
        }
        private val sample = object : Runnable {
            override fun run() {
                if (!activityResumed || !meterAttached || windowVisibility != View.VISIBLE) return
                levels.addLast(RecordingService.currentAmplitude.coerceIn(0, 32767))
                while (levels.size > 60) levels.removeFirst()
                invalidate(); postDelayed(this, 100)
            }
        }
        fun updateSampling() {
            removeCallbacks(sample)
            if (activityResumed && isAttachedToWindow && windowVisibility == View.VISIBLE) post(sample)
        }
        override fun onAttachedToWindow() { super.onAttachedToWindow(); meterAttached = true; updateSampling() }
        override fun onWindowVisibilityChanged(visibility: Int) {
            super.onWindowVisibilityChanged(visibility)
            if (meterAttached) updateSampling()
        }
        override fun onDetachedFromWindow() { meterAttached = false; removeCallbacks(sample); super.onDetachedFromWindow() }
        override fun onDraw(canvas: android.graphics.Canvas) {
            super.onDraw(canvas)
            // No idle animation or generated bars: each bar is one measured microphone peak.
            val spacing = dp(6).toFloat()
            val count = minOf(60, (width / spacing).toInt())
            val values = levels.toList().takeLast(count)
            val left = (width - (count - 1) * spacing) / 2f
            values.forEachIndexed { index, value ->
                if (value > 0) {
                    val half = (height * 0.42f * kotlin.math.sqrt(value / 32767f)).coerceAtLeast(dp(1).toFloat())
                    val x = left + (count - values.size + index) * spacing
                    canvas.drawLine(x, height / 2f - half, x, height / 2f + half, paint)
                }
            }
        }
    }
    private fun recordControl(title: String, icon: Int, stop: Boolean, enabled: Boolean, action: () -> Unit): LinearLayout = column().apply {
        gravity = Gravity.CENTER
        addView(ImageButton(this@MainActivity).apply {
            setImageResource(icon); contentDescription = title; tooltipText = title
            imageTintList = android.content.res.ColorStateList.valueOf(if (stop) deleteRed else ink)
            val circle = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(if (stop) getColor(R.color.vemo_design_destructivesurface) else subtle)
                setStroke(dp(1), if (stop) Color.rgb(238, 212, 214) else line)
            }
            background = android.graphics.drawable.RippleDrawable(ripple, circle, null)
            setPadding(dp(20), dp(20), dp(20), dp(20)); isEnabled = enabled; alpha = if (enabled) 1f else 0.4f
            setOnClickListener { action() }
        }, LinearLayout.LayoutParams(dp(if (stop) 68 else 56), dp(if (stop) 68 else 56)))
        addView(text(title, 13f, muted).apply { gravity = Gravity.CENTER; setPadding(dp(4), dp(11), dp(4), 0) })
    }
    private fun startRecording() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) { requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 41); return }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED && !vemo().preferences.getBoolean("notification_asked", false)) {
            vemo().preferences.edit().putBoolean("notification_asked", true).apply()
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 42)
            return
        }
        stopPlayer()
        try { startForegroundService(Intent(this, RecordingService::class.java).setAction("com.vemo.START").putExtra("com.vemo.FOLDER_ID", selectedFolder)) }
        catch (error: Exception) { vemo().notify(safeError(error)) }
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 41 && grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED || requestCode == 42) startRecording()
        else vemo().notify("녹음하려면 설정에서 마이크 권한을 허용하세요.")
    }
    private fun command(action: String) { startService(Intent(this, RecordingService::class.java).setAction("com.vemo.$action")) }
    private fun wideLibrary() = resources.configuration.screenWidthDp >= 600 && resources.configuration.fontScale < 1.5f
    private fun compactLibrary() = resources.configuration.screenHeightDp <= 480 && wideLibrary()
    private fun recordings() {
        val compact = compactLibrary()
        search = EditText(this).apply {
            hint = "녹음 제목 검색"; contentDescription = "녹음 검색"; textSize = 15f
            inputType = InputType.TYPE_CLASS_TEXT; setSingleLine(true)
            minHeight = dp(52); setPadding(dp(12), dp(12), dp(12), dp(12))
            background = surface(subtle); setTextColor(ink); setHintTextColor(muted)
            setText(query)
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                    query = s?.toString().orEmpty(); updateEntries()
                }
                override fun afterTextChanged(s: Editable?) = Unit
            })
        }
        val searchBar = row().apply {
            background = surface(subtle).apply { cornerRadius = dp(12).toFloat() }
            setPadding(dp(14), 0, dp(4), 0)
            addView(ImageView(this@MainActivity).apply {
                setImageResource(R.drawable.ic_search); imageTintList = android.content.res.ColorStateList.valueOf(muted)
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, LinearLayout.LayoutParams(dp(20), dp(20)))
            search?.background = null
            addView(search, LinearLayout.LayoutParams(0, -2, 1f))
        }
        content.addView(searchBar, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(if (compact) 8 else 18) })
        libraryError?.let { content.addView(text(it, 14f, deleteRed)) }
        if (vemo().store.unreadableCount() > 0) content.addView(text("읽을 수 없는 녹음 메타데이터가 있습니다. 원본은 보존했습니다. 앱을 삭제하지 마세요.", 14f, muted))
        val body = if (wideLibrary()) row().apply { gravity = Gravity.TOP } else column()
        val navigation = column()
        val snapshot = library()
        val folderIds = snapshot?.folders.orEmpty().map { it.id }.toSet()
        val collapsedFolders = vemo().preferences.getStringSet("collapsed_library_folders", emptySet()).orEmpty()
            .intersect(folderIds).toMutableSet()
        fun filter(name: String, id: String?, onlyUnfiled: Boolean, count: Int, depth: Int = 0, hasChildren: Boolean = false) {
            val selected = selectedFolder == id && unfiled == onlyUnfiled
            val color = if (selected) purple else ink
            val control = row().apply {
                minimumHeight = dp(48)
                isSelected = selected; isClickable = true; isFocusable = true
                val fill = if (selected) selection else Color.WHITE
                background = android.graphics.drawable.RippleDrawable(ripple, surface(fill), null)
                setPadding(dp(if (id == null) 12 else 0), dp(8), dp(4), dp(8))
                setOnClickListener { selectedFolder = id; unfiled = onlyUnfiled; render() }
            }
            val icon = if (id != null) R.drawable.ic_folder else if (onlyUnfiled) R.drawable.ic_inbox else R.drawable.ic_library
            control.addView(ImageView(this).apply {
                setImageResource(icon); imageTintList = android.content.res.ColorStateList.valueOf(color)
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, LinearLayout.LayoutParams(dp(20), dp(20)).apply { marginEnd = dp(12) })
            val nameView = text(name, 15f, color, selected).apply {
                maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }
            control.addView(nameView, LinearLayout.LayoutParams(0, -2, 1f))
            val countView = text(java.text.NumberFormat.getIntegerInstance().format(count), 12f, muted).apply { importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO }
            control.addView(countView, LinearLayout.LayoutParams(-2, -2).apply { marginStart = dp(8); marginEnd = dp(8) })
            control.addView(ImageView(this@MainActivity).apply {
                setImageResource(R.drawable.ic_check)
                imageTintList = android.content.res.ColorStateList.valueOf(purple)
                visibility = if (selected) View.VISIBLE else View.INVISIBLE
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, LinearLayout.LayoutParams(dp(16), dp(16)))
            control.tag = depth
            if (id != null) {
                holds.add(LibraryHold(control, LibraryDrag(id, true), ::freezeInteraction) { folderActions(id) })
                dropTarget(control, id)
            }
            folderControls.add(FolderControl(control, nameView, countView, id, onlyUnfiled))
            val folderRow = if (id != null) row().apply {
                setPadding(dp(depth.coerceAtMost(4) * 14), 0, 0, 0)
                val collapsed = id in collapsedFolders
                if (hasChildren) addView(headerAction(R.drawable.ic_chevron, "$name, " + if (collapsed) "접힘, 펼치기" else "펼쳐짐, 접기") {
                    if (!interactionFrozen) {
                        rememberFolderFocus("fold:$id")
                        if (!collapsedFolders.add(id)) collapsedFolders.remove(id)
                        vemo().preferences.edit().putStringSet("collapsed_library_folders", collapsedFolders.toSet()).apply()
                        val scrollY = mainScroll?.scrollY ?: 0
                        render()
                        mainScroll?.let { scroll -> scroll.post { scroll.scrollTo(0, scrollY) } }
                    }
                }.apply {
                    folderActionViews["fold:$id"] = this
                    // Rotate just the glyph: keep the adjacent selection/drag target independent.
                    setImageDrawable(getDrawable(R.drawable.ic_chevron)?.mutate()?.let { glyph ->
                        android.graphics.drawable.RotateDrawable().apply {
                            drawable = glyph; fromDegrees = if (collapsed) 0f else 90f; toDegrees = fromDegrees; level = 10000
                        }
                    })
                    setPadding(dp(15), dp(15), dp(15), dp(15))
                    if (Build.VERSION.SDK_INT >= 30) stateDescription = if (collapsed) "접힘" else "펼쳐짐"
                }) else addView(Space(this@MainActivity), LinearLayout.LayoutParams(dp(48), 1))
                addView(control, LinearLayout.LayoutParams(0, -2, 1f))
                addView(headerAction(R.drawable.ic_more, "$name 폴더 관리") { folderActions(id) }.apply { folderActionViews["more:$id"] = this })
            } else control
            navigation.addView(folderRow, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(2) })
        }
        val items = vemo().store.list()
        fun standardFilters() {
            filter("전체 녹음", null, false, items.size)
            filter("미분류", null, true, items.count { snapshot?.recordingFolders?.get(it.id) == null })
        }
        standardFilters()
        navigation.addView(row().apply {
            addView(text("폴더", 13f, muted, true), LinearLayout.LayoutParams(0, -2, 1f))
            addView(button("＋ 폴더") { rememberFolderFocus("create-folder"); folderEditor(null, selectedFolder) }.apply {
                folderActionViews["create-folder"] = this
                minHeight = resources.getDimensionPixelSize(R.dimen.vemo_design_touch_min)
                val pillInset = (resources.getDimensionPixelSize(R.dimen.vemo_design_touch_min) - resources.getDimensionPixelSize(R.dimen.vemo_design_folder_pill)) / 2
                background = android.graphics.drawable.InsetDrawable(
                    android.graphics.drawable.RippleDrawable(
                        ripple,
                        surface(selection), null),
                    0, pillInset, 0, pillInset)
                setPadding(dp(12), dp(8), dp(12), dp(8))
                textSize = 13f
            }, LinearLayout.LayoutParams(-2, -2))
        })
        val children = snapshot?.folders.orEmpty().groupBy { it.parentId }
        val pending = java.util.ArrayDeque<Pair<LibraryFolder, Int>>()
        children[null].orEmpty().sortedByDescending { it.name }.forEach { pending.addLast(it to 0) }
        val visited = mutableSetOf<String>()
        // Validated metadata is a tree; the visited set also protects rendering against cycles.
        while (pending.isNotEmpty() && visited.size < snapshot?.folders.orEmpty().size) {
            val (folder, depth) = pending.removeLast()
            if (!visited.add(folder.id)) continue
            val descendants = children[folder.id].orEmpty()
            filter(folder.name, folder.id, false, items.count { snapshot?.recordingFolders?.get(it.id) == folder.id }, depth, descendants.isNotEmpty())
            if (folder.id !in collapsedFolders) descendants.sortedByDescending { it.name }.forEach { pending.addLast(it to depth + 1) }
        }
        if (snapshot != null && snapshot.folders.isEmpty()) navigation.addView(text("폴더를 만들어 녹음을 정리하세요.", 14f, muted))
        val list = column()
        list.addView(row().apply {
            addView(text(if (selectedFolder == null && !unfiled) "최근 녹음" else "폴더의 녹음", 14f, muted, true), LinearLayout.LayoutParams(0, -2, 1f))
            if (vemo().cloudConfigured()) {
                syncAction = ImageButton(this@MainActivity).apply {
                    setImageResource(R.drawable.ic_refresh)
                    scaleType = ImageView.ScaleType.CENTER_INSIDE
                    imageTintList = android.content.res.ColorStateList.valueOf(purple)
                    background = android.graphics.drawable.RippleDrawable(ripple, surface(Color.TRANSPARENT), null)
                    setPadding(dp(12), dp(12), dp(12), dp(12))
                    stateListAnimator = null; elevation = 0f
                    setOnClickListener { vemo().sync(true) }
                }
                addView(syncAction, LinearLayout.LayoutParams(dp(48), dp(48)))
                updateSyncAction()
            }
        })
        entries = column(); list.addView(entries)
        if (wideLibrary()) {
            body.addView(navigation, LinearLayout.LayoutParams(dp(240), -2).apply { marginEnd = dp(24) })
            body.addView(list, LinearLayout.LayoutParams(0, -2, 1f))
        } else { body.addView(navigation); body.addView(list) }
        content.addView(body)
        updateEntries()
    }
    private fun updateEntries() {
        if (interactionFrozen) { refreshPending = true; return }
        val target = entries ?: return
        holds.removeAll { hold -> if (hold.belongsTo(target)) { hold.cancel(); true } else false }
        target.removeAllViews()
        val snapshot = library()
        val allItems = vemo().store.list()
        for (control in folderControls) {
            val count = allItems.count {
                if (control.unfiled) snapshot?.recordingFolders?.get(it.id) == null
                else control.folderId == null || snapshot?.recordingFolders?.get(it.id) == control.folderId
            }
            control.count.text = java.text.NumberFormat.getIntegerInstance().format(count)
            val depth = (control.view.tag as? Int) ?: 0
            val path = control.folderId?.let { id ->
                val names = mutableListOf<String>()
                val byId = snapshot?.folders.orEmpty().associateBy { it.id }
                var current = byId[id]
                val visited = mutableSetOf<String>()
                while (current != null && visited.add(current.id)) {
                    names.add(current.name); current = byId[current.parentId]
                }
                names.asReversed().joinToString(" / ")
            } ?: control.name.text.toString()
            control.view.contentDescription = "$path, $count 녹음" +
                (if (depth > 0) ", 하위 폴더 깊이 $depth" else "") +
                (if (control.view.isSelected) ", 선택됨" else "")
        }
        val items = allItems.filter {
            snapshot == null || (if (unfiled) snapshot.recordingFolders[it.id] == null
            else selectedFolder == null || snapshot.recordingFolders[it.id] == selectedFolder)
        }
        var shown = 0
        for (item in items) {
            val result = try { vemo().store.result(item) } catch (_: Exception) { null }
            val title = result?.title ?: item.title
            if (query.isNotBlank() && !title.contains(query.trim(), ignoreCase = true)) continue
            shown++
            // Only known successful terminal messages may disappear from the compact row.
            val completedMessages = setOf("", "기기에 저장됨", "서버 저장 완료 · 녹음만", "PC 전사·노트 수신 완료", "선택한 폴더에 복사 완료 · 기기 원본 보존")
            val pending = if (item.state == CaptureState.COMPLETE && item.mode == Mode.DESKTOP && result == null)
                if (item.uploaded) "PC 결과 대기 중" else "업로드 대기 · 기기 원본 저장됨" else null
            val visibleStatus = listOfNotNull(
                state(item).takeIf { item.state != CaptureState.COMPLETE },
                pending,
                item.status.takeIf { it !in completedMessages }
            ).distinct().joinToString(" · ")
            target.addView(column().apply {
                setPadding(0, dp(6), 0, dp(6)); minimumHeight = resources.getDimensionPixelSize(R.dimen.vemo_design_row)
                gravity = Gravity.CENTER_VERTICAL
                addView(row().apply {
                    addView(text(title, 16f, ink).apply {
                        maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
                    }, LinearLayout.LayoutParams(0, -2, 1f))
                    val recordedDate = try {
                        OffsetDateTime.parse(item.recordedAt).format(DateTimeFormatter.ofPattern("MM.dd"))
                    } catch (_: Exception) { item.recordedAt.substringBefore('T') }
                    addView(text(recordedDate, 12f, muted).apply {
                        setSingleLine(true); gravity = Gravity.END
                    }, LinearLayout.LayoutParams(-2, -2).apply { marginStart = dp(12) })
                    importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS
                }, LinearLayout.LayoutParams(-1, -2))
                if (visibleStatus.isNotBlank()) addView(text(visibleStatus, 12f,
                    if (item.state in setOf(CaptureState.FAILED, CaptureState.INTERRUPTED)) deleteRed else muted).apply {
                    setPadding(0, 0, 0, dp(4))
                    importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
                })
                isClickable = true; isFocusable = true
                background = android.graphics.drawable.RippleDrawable(
                    ripple,
                    surface(this@MainActivity.background).apply { cornerRadius = 0f }, null)
                contentDescription = "$title, ${date(item.recordedAt)}, ${duration(item.durationMs)}, ${state(item)}, $visibleStatus, 열기"
                setOnClickListener { openRecording(item.id) }
                holds.add(LibraryHold(this, LibraryDrag(item.id, false), ::freezeInteraction) { recordingActions(item.id) })
            }, LinearLayout.LayoutParams(-1, -2))
            divider(target)
        }
        if (shown == 0) target.addView(emptyState(
            if (query.isNotBlank()) R.drawable.ic_search else R.drawable.ic_library,
            if (query.isNotBlank()) "검색 결과가 없어요" else "아직 녹음이 없어요",
            if (query.isNotBlank()) "다른 제목으로 다시 검색해 보세요." else "첫 번째 이야기를 남겨 보세요.",
            if (query.isNotBlank()) null else "새 녹음"
        ) { stopPlayer(); detailId = null; tab = "record"; render() })
    }
    private fun emptyState(icon: Int, title: String, description: String, actionTitle: String? = null,
        enabled: Boolean = true, compact: Boolean = false, action: () -> Unit = {}): LinearLayout = column().apply {
        layoutParams = LinearLayout.LayoutParams(-1, -2)
        gravity = Gravity.CENTER
        minimumHeight = if (compact) 0 else dp(240)
        setPadding(dp(25), dp(if (compact) 18 else 35), dp(25), dp(if (compact) 18 else 35))
        if (compact) background = surface(subtle)
        addView(ImageView(this@MainActivity).apply {
            setImageResource(icon); imageTintList = android.content.res.ColorStateList.valueOf(Color.parseColor("#B5AFC4"))
            importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
        }, LinearLayout.LayoutParams(dp(if (compact) 24 else 36), dp(if (compact) 24 else 36)).apply { bottomMargin = dp(18) })
        addView(text(title, 17f, ink, true).apply {
            gravity = Gravity.CENTER; setPadding(0, 0, 0, 0)
        }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(9) })
        addView(text(description, 12f, muted).apply {
            gravity = Gravity.CENTER; setPadding(0, 0, 0, 0); setLineSpacing(0f, 1.8f)
        }, LinearLayout.LayoutParams(-1, -2))
        if (actionTitle != null) addView(compactAction(actionTitle, outlined = true, action = action).apply {
            isEnabled = enabled
            textSize = 12f; minWidth = dp(76); minimumWidth = dp(76)
            setPadding(dp(16), dp(8), dp(16), dp(8))
        }, LinearLayout.LayoutParams(-2, -2).apply { topMargin = dp(21) })
    }
    private fun syncProblemPanel(problem: SyncProblem, compact: Boolean): LinearLayout {
        val title = when (problem) {
            SyncProblem.CONNECTION -> "연결을 확인해 주세요"
            SyncProblem.AUTH -> "연결 승인을 확인해 주세요"
            SyncProblem.CONFLICT -> "원격 파일을 확인해 주세요"
            SyncProblem.VALIDATION -> "동기화 설정을 확인해 주세요"
            SyncProblem.OTHER -> "동기화를 완료하지 못했어요"
        }
        val body = when (problem) {
            SyncProblem.CONNECTION -> "기존 녹음과 결과는 그대로 보관되어 있어요.\n연결 상태를 확인한 뒤 다시 시도해 주세요."
            SyncProblem.AUTH -> "기존 녹음과 결과는 그대로 보관되어 있어요.\n설정에서 로그인과 접근 권한을 확인해 주세요."
            SyncProblem.CONFLICT -> "원격 콘텐츠와 충돌해 덮어쓰지 않았어요.\n기기 원본과 이전 결과는 그대로 보관되어 있어요."
            SyncProblem.VALIDATION -> "연결된 폴더와 PC 처리기 정보를 확인해 주세요.\n기존 녹음과 결과는 그대로 보관되어 있어요."
            SyncProblem.OTHER -> "저장 공간·폴더 권한·연결을 확인해 주세요.\n기존 녹음과 결과는 그대로 보관되어 있어요."
        }
        val settings = problem == SyncProblem.AUTH || problem == SyncProblem.VALIDATION || !vemo().cloudConfigured()
        return emptyState(R.drawable.ic_warning, title, body,
            if (settings) "연결 설정" else "다시 시도", !vemo().gate.busy(), compact) {
            if (settings) { stopPlayer(); tab = "settings"; detailId = null; render() } else vemo().sync(true)
        }
    }
    private fun freezeInteraction(frozen: Boolean) {
        interactionFrozen = frozen
        if (!frozen && refreshPending) {
            refreshPending = false
            val scrollY = mainScroll?.scrollY ?: 0
            handler.post {
                if (!isFinishing && !isDestroyed) {
                    render()
                    mainScroll?.post { mainScroll?.scrollTo(0, scrollY) }
                }
            }
        }
    }
    private fun finishInteraction() {
        // Drag-ended is delivered to all targets; replace views only on the next UI turn.
        holds.forEach { it.cancel() }
        freezeInteraction(false)
    }
    private fun openRecording(id: String) {
        stopPlayer(); detailId = id; detailTab = "transcript"; render()
    }
    private fun folderPath(state: LibraryState, id: String): String {
        val byId = state.folders.associateBy { it.id }
        val names = mutableListOf<String>()
        val visited = mutableSetOf<String>()
        var folder = byId[id]
        while (folder != null && visited.add(folder.id)) {
            names.add(folder.name); folder = byId[folder.parentId]
        }
        return names.asReversed().joinToString(" / ")
    }
    private fun canMove(item: LibraryDrag, target: String?, state: LibraryState): Boolean {
        val byId = state.folders.associateBy { it.id }
        if (target != null && target !in byId) return false
        if (!item.folder) return state.recordingFolders[item.id] != target
        val source = byId[item.id] ?: return false
        if (source.parentId == target) return false
        if (state.folders.any { it.id != source.id && it.parentId == target && it.name == source.name }) return false
        var ancestor = target
        val visited = mutableSetOf<String>()
        while (ancestor != null) {
            if (ancestor == item.id || !visited.add(ancestor)) return false
            ancestor = byId[ancestor]?.parentId
        }
        return true
    }
    private fun moveItem(item: LibraryDrag, target: String?) {
        val current = vemo().library.snapshot()
        check(canMove(item, target, current)) { "현재 위치, 자기 자신 또는 하위 폴더로 이동할 수 없습니다." }
        if (item.folder) vemo().library.moveFolder(item.id, target)
        else {
            vemo().store.get(item.id) // Reject an item removed while its menu was open.
            vemo().library.assign(item.id, target)
        }
    }
    private fun dropTarget(view: View, target: String?) {
        val normal = view.background
        var accepted = false
        view.setOnDragListener { _, event ->
            val item = event.localState as? LibraryDrag ?: return@setOnDragListener false
            when (event.action) {
                DragEvent.ACTION_DRAG_STARTED -> {
                    accepted = try { canMove(item, target, vemo().library.snapshot()) } catch (_: Exception) { false }
                    accepted
                }
                DragEvent.ACTION_DRAG_ENTERED -> {
                    if (accepted) view.background = surface(selection, true)
                    accepted
                }
                DragEvent.ACTION_DRAG_LOCATION -> accepted
                DragEvent.ACTION_DRAG_EXITED -> { view.background = normal; accepted }
                DragEvent.ACTION_DROP -> {
                    view.background = normal
                    if (!accepted) false else {
                        val moved = libraryAction { moveItem(item, target) }
                        if (moved) {
                            refreshPending = true
                            view.announceForAccessibility("이동했습니다")
                        }
                        moved
                    }
                }
                DragEvent.ACTION_DRAG_ENDED -> { view.background = normal; accepted = false; true }
                else -> false
            }
        }
    }

    /** App-owned dialogs share a rounded, scrollable, keyboard-resizing native surface. */
    private fun management(title: String, build: (Dialog, LinearLayout, TextView) -> Unit) {
        val returnFocus = currentFocus
        val returnFolderKey = folderFocusKey ?: folderActionViews.entries.firstOrNull { it.value.hasFocus() || it.value.isAccessibilityFocused }?.key
        val returnAccessibility = returnFolderKey?.let { folderActionViews[it]?.isAccessibilityFocused } == true || folderAccessibilityFocus
        managementDialog?.dismiss()
        val dialog = Dialog(this)
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE)
        val body = column().apply { setPadding(dp(24), dp(20), dp(24), dp(20)) }
        body.addView(text(title, 22f, ink, true))
        val error = text("", 14f, deleteRed).apply {
            visibility = View.GONE; accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE
        }
        body.addView(error)
        build(dialog, body, error)
        body.addView(button("취소") { dialog.dismiss() })
        val scroll = object : ScrollView(this) {
            override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
                val available = if (MeasureSpec.getMode(heightMeasureSpec) == MeasureSpec.UNSPECIFIED)
                    dp(resources.configuration.screenHeightDp) else MeasureSpec.getSize(heightMeasureSpec)
                val cap = minOf(available, dp(resources.configuration.screenHeightDp - 48)).coerceAtLeast(dp(48))
                super.onMeasure(widthMeasureSpec, MeasureSpec.makeMeasureSpec(cap, MeasureSpec.AT_MOST))
            }
        }.apply { addView(body); clipToPadding = false }
        dialog.setContentView(scroll)
        dialog.setCanceledOnTouchOutside(true)
        dialog.window?.apply {
            setBackgroundDrawable(GradientDrawable().apply { setColor(Color.WHITE); cornerRadius = resources.getDimension(R.dimen.vemo_design_sheet_radius) })
            decorView.clipToOutline = true
            addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            setDimAmount(0.28f)
            setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        }
        dialog.setOnDismissListener {
            if (managementDialog === dialog) {
                managementDialog = null
                if (returnFolderKey != null) {
                    folderFocusKey = returnFolderKey; folderAccessibilityFocus = returnAccessibility
                    restoreActionFocus()
                } else if (returnFocus?.isAttachedToWindow == true && activityResumed && !isFinishing && !isDestroyed) {
                    val root = container
                    returnFocus.post {
                        if (activityResumed && !isFinishing && !isDestroyed && managementDialog == null &&
                            container === root && returnFocus.isAttachedToWindow && folderFocusKey == null) returnFocus.requestFocus()
                    }
                }
            }
        }
        managementDialog = dialog
        dialog.show()
        dialog.window?.setLayout(minOf(dp(480), dp(resources.configuration.screenWidthDp) - dp(40)), WindowManager.LayoutParams.WRAP_CONTENT)
    }
    private fun choiceDialog(title: String, choices: List<String>, selected: Int? = null, choose: (Int) -> Unit) {
        management(title) { dialog, body, _ ->
            choices.forEachIndexed { index, caption ->
                val action = { dialog.dismiss(); choose(index) }
                if (selected == null) body.addView(button(caption) { action() }.apply {
                    gravity = Gravity.START or Gravity.CENTER_VERTICAL
                }) else body.addView(RadioButton(this).apply {
                    text = caption; textSize = 15f; setTextColor(ink)
                    buttonTintList = android.content.res.ColorStateList.valueOf(purple)
                    isChecked = index == selected
                    minHeight = dp(48); setPadding(dp(8), dp(12), dp(12), dp(12))
                    background = android.graphics.drawable.RippleDrawable(ripple, surface(Color.WHITE), null)
                    setOnClickListener { action() }
                }, LinearLayout.LayoutParams(-1, -2))
            }
        }
    }
    private fun libraryAction(errorView: TextView? = null, action: () -> Unit): Boolean = try {
        action(); true
    } catch (error: Exception) {
        val message = error.message ?: "작업을 완료하지 못했습니다. 원본 녹음은 보존했습니다."
        if (errorView != null) {
            errorView.text = message; errorView.visibility = View.VISIBLE
            errorView.post { errorView.requestRectangleOnScreen(android.graphics.Rect(0, 0, errorView.width, errorView.height), true) }
        } else management("작업을 완료할 수 없습니다") { _, body, _ -> body.addView(text(message, 15f, muted)) }
        false
    }
    private fun folderEditor(id: String?, parent: String? = null) {
        val snapshot = library() ?: run { libraryAction { error(libraryError ?: "폴더 정보를 읽을 수 없습니다.") }; return }
        val folder = snapshot.folders.firstOrNull { it.id == id }
        management(if (id == null) "새 폴더" else "폴더 이름 변경") { dialog, body, error ->
            val parents = listOf<String?>(null) + snapshot.folders.map { it.id }.sortedBy { folderPath(snapshot, it) }
            val parentPicker = if (id == null) Spinner(this).apply {
                contentDescription = "상위 폴더"; minimumHeight = dp(48)
                adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, parents.map { if (it == null) "최상위 폴더" else folderPath(snapshot, it) })
                setSelection(parents.indexOf(parent).coerceAtLeast(0))
                body.addView(text("상위 폴더", 13f, muted)); body.addView(this, LinearLayout.LayoutParams(-1, -2))
            } else null
            if (id != null) body.addView(text(if (parent == null) "최상위 폴더" else folderPath(snapshot, parent), 14f, muted))
            val edit = EditText(this).apply {
                hint = "폴더 이름"; contentDescription = "폴더 이름"; setSingleLine(true)
                setText(folder?.name.orEmpty()); setSelectAllOnFocus(true); minHeight = dp(52)
                setTextColor(ink); setHintTextColor(muted); textSize = 16f
                setPadding(dp(16), dp(12), dp(16), dp(12)); background = surface(subtle)
            }
            body.addView(edit, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(12); bottomMargin = dp(12) })
            body.removeView(error); body.addView(error)
            body.addView(button("저장", true) {
                if (libraryAction(error) {
                    if (id == null) {
                        val chosenParent = parents[parentPicker?.selectedItemPosition ?: 0]
                        check(chosenParent == null || vemo().library.snapshot().folders.any { it.id == chosenParent }) { "상위 폴더가 변경되었습니다. 다시 선택하세요." }
                        vemo().library.createFolder(edit.text.toString().trim(), chosenParent)
                    } else vemo().library.renameFolder(id, edit.text.toString().trim())
                }) { dialog.dismiss(); render() }
            })
        }
    }
    private fun folderActions(id: String) {
        rememberFolderFocus("more:$id")
        val folder = library()?.folders?.firstOrNull { it.id == id } ?: return
        management(folder.name) { dialog, body, _ ->
            body.addView(View(this).apply { background = surface(line) }, 0, LinearLayout.LayoutParams(dp(34), dp(4)).apply { gravity = Gravity.CENTER_HORIZONTAL; bottomMargin = dp(16) })
            fun action(title: String, color: Int = purple, run: () -> Unit) { body.addView(button(title) { dialog.dismiss(); run() }.apply {
                setTextColor(color); gravity = Gravity.START or Gravity.CENTER_VERTICAL
                background = android.graphics.drawable.RippleDrawable(ripple, surface(Color.WHITE), null)
            }) }
            action("하위 폴더 만들기") { folderEditor(null, id) }
            action("이름 변경") { folderEditor(id, folder.parentId) }
            action("폴더 이동") { moveChooser(LibraryDrag(id, true)) }
            action("폴더 삭제", deleteRed) {
                management("폴더를 삭제할까요?") { confirm, section, error ->
                    section.addView(text("이 폴더와 하위 폴더를 삭제합니다. 녹음은 삭제하지 않고 미분류로 옮깁니다.", 15f, muted))
                    section.addView(button("삭제") {
                        if (libraryAction(error) { vemo().library.deleteFolder(id) }) {
                            confirm.dismiss(); render()
                        }
                    }.apply { setTextColor(deleteRed) })
                }
            }
        }
        managementDialog?.window?.setGravity(Gravity.BOTTOM)
    }
    private fun recordingActions(id: String) {
        val item = try { vemo().store.get(id) } catch (error: Exception) { libraryAction { throw error }; return }
        val title = try { vemo().store.result(item)?.title ?: item.title } catch (_: Exception) { item.title }
        management(title) { dialog, body, _ ->
            fun action(title: String, run: () -> Unit) { body.addView(button(title) { dialog.dismiss(); run() }) }
            action("열기") { openRecording(id) }
            action("폴더 이동") { moveRecording(id) }
            if (item.uploadable) action("공유") { shareRecording(id) }
            if (item.state !in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) {
                action("목록에서 숨기기") { hideRecording(id) }
            }
        }
    }
    private fun shareRecording(id: String) {
        libraryAction {
            val item = vemo().store.get(id)
            check(item.uploadable) { "완료한 녹음만 공유할 수 있습니다." }
            startActivity(Intent.createChooser(ShareAudio.intent(this, item), "녹음 공유"))
        }
    }
    private fun hideRecording(id: String) {
        management("목록에서 숨길까요?") { dialog, body, error ->
            body.addView(text("기기 원본은 보존합니다. 클라우드 파일은 삭제하지 않으며 이 항목을 자동으로 다시 가져오지 않습니다.", 15f, muted))
            body.addView(button("숨기기") {
                if (libraryAction(error) {
                    val item = vemo().store.get(id)
                    check(item.state !in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) { "진행 중인 녹음은 숨길 수 없습니다." }
                    vemo().store.hide(id)
                }) { dialog.dismiss(); if (detailId == id) detailId = null; render() }
            })
        }
    }
    private fun moveRecording(id: String) = moveChooser(LibraryDrag(id, false))
    private fun moveChooser(item: LibraryDrag) {
        val snapshot = library() ?: run { libraryAction { error(libraryError ?: "폴더 정보를 읽을 수 없습니다.") }; return }
        management(if (item.folder) "폴더 이동" else "녹음 이동") { dialog, body, error ->
            body.addView(text("이동할 위치를 선택하세요.", 14f, muted))
            val destinations = listOf<String?>(null) + snapshot.folders.sortedBy { folderPath(snapshot, it.id) }.map { it.id }
            val valid = destinations.filter { canMove(item, it, snapshot) }
            if (valid.isEmpty()) body.addView(text("이동할 다른 폴더가 없습니다.", 15f, muted))
            else {
                val picker = Spinner(this).apply {
                    contentDescription = "이동할 위치"; minimumHeight = dp(48)
                    adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, valid.map { target ->
                        if (target == null) { if (item.folder) "최상위 폴더" else "미분류" } else folderPath(snapshot, target)
                    })
                }
                body.addView(picker, LinearLayout.LayoutParams(-1, -2))
                body.removeView(error); body.addView(error)
                body.addView(button("이동", true) {
                    val target = valid[picker.selectedItemPosition]
                    if (libraryAction(error) { moveItem(item, target) }) { dialog.dismiss(); render() }
                })
            }
        }
    }
    private fun detailActions(id: String) {
        val item = try { vemo().store.get(id) } catch (error: Exception) { libraryAction { throw error }; return }
        management("녹음 더보기") { dialog, body, _ ->
            body.addView(text("${state(item)} · ${item.bytes / 1024} KB", 13f, muted))
            if (item.status.isNotBlank()) body.addView(text(item.status, 13f, muted))
            fun action(title: String, enabled: Boolean = true, run: () -> Unit) {
                body.addView(button(title) { dialog.dismiss(); run() }.apply {
                    isEnabled = enabled; gravity = Gravity.START or Gravity.CENTER_VERTICAL
                    background = android.graphics.drawable.RippleDrawable(ripple, surface(Color.WHITE), null)
                })
            }
            action("폴더 이동") { moveRecording(id) }
            if (item.uploadable) {
                action("폴더에 복사") { vemo().export(id) }
                if (vemo().cloudConfigured()) action("동기화 다시 시도 / PC 결과 확인", !vemo().gate.busy()) { vemo().sync(true) }
            } else if (item.state == CaptureState.INTERRUPTED) action("보존한 오디오 복구 시도") { vemo().recover(id) }
            if (item.state !in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING))
                action("목록에서 숨기기") { hideRecording(id) }
        }
        managementDialog?.window?.apply {
            setGravity(Gravity.BOTTOM)
            setLayout(minOf(dp(480), dp(resources.configuration.screenWidthDp)), WindowManager.LayoutParams.WRAP_CONTENT)
        }
    }
    private fun detail(id: String) {
        val item = try { vemo().store.get(id) } catch (_: Exception) { detailId = null; recordings(); return }
        val result = try { vemo().store.result(item) } catch (_: Exception) { null }
        content.addView(text(if (result != null) "PC에서 받은 결과" else state(item), 13f, muted))
        gap(12)
        label(result?.title ?: item.title, 24f)
        gap(8)
        content.addView(text("${date(item.recordedAt)} · ${duration(item.durationMs)}", 13f, muted))
        val folder = library()?.recordingFolders?.get(id)
        val path = if (folder == null) "미분류" else try { vemo().library.path(folder) } catch (_: Exception) { "폴더 확인 필요" }
        content.addView(text(path, 14f, purple).apply {
            setCompoundDrawablesRelativeWithIntrinsicBounds(R.drawable.ic_folder, 0, 0, 0)
            compoundDrawablePadding = dp(8); setPadding(0, dp(12), 0, dp(12)); minimumHeight = dp(48)
            isClickable = true; isFocusable = true; contentDescription = "$path, 폴더 이동"
            background = android.graphics.drawable.RippleDrawable(ripple, surface(Color.WHITE), null)
            folderActionViews["breadcrumb:$id"] = this
            setOnClickListener { rememberFolderFocus("breadcrumb:$id"); moveRecording(id) }
        })
        if (item.state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) {
            content.addView(compactAction("진행 중인 녹음 열기") { tab = "record"; detailId = null; render() })
        }
        if (item.uploadable) {
            content.addView((if (resources.configuration.fontScale > 1.3f) column() else row()).apply {
                val stacked = orientation == LinearLayout.VERTICAL
                addView(compactAction("녹음 듣기", R.drawable.ic_play, true) { play(item) }.apply {
                    isEnabled = !vemo().gate.isRecording()
                }, if (stacked) LinearLayout.LayoutParams(-1, -2) else LinearLayout.LayoutParams(0, -2, 1f).apply { marginEnd = dp(8) })
                addView(compactAction("오디오 공유", R.drawable.ic_share) { shareRecording(id) }, if (stacked) LinearLayout.LayoutParams(-1, -2) else LinearLayout.LayoutParams(0, -2, 1f))
            })
            if (playerId == item.id) content.addView(playbackPanel())
        }
        gap(20)
        val tabs = row().apply { background = surface(subtle); setPadding(dp(4), dp(4), dp(4), dp(4)) }
        for ((key, title) in listOf("transcript" to "전사", "notes" to "노트")) {
            tabs.addView(compactAction(title, if (key == "transcript") R.drawable.ic_transcript else R.drawable.ic_notes) { detailTab = key; render() }.apply {
                isSelected = detailTab == key
                background = android.graphics.drawable.RippleDrawable(ripple, surface(if (isSelected) selection else subtle), null)
                setTextColor(if (isSelected) purple else muted)
                contentDescription = title + if (isSelected) ", 선택됨" else ""
            }, LinearLayout.LayoutParams(0, -2, 1f))
        }
        content.addView(tabs, LinearLayout.LayoutParams(if (resources.configuration.fontScale > 1.3f) -1 else dp(200), -2))
        gap(24)
        val syncProblem = vemo().syncFeedback.problem(id)
        if (result != null) {
            if (syncProblem != null) content.addView(syncProblemPanel(syncProblem, compact = true))
            val body = if (detailTab == "notes") result.notes else result.transcript
            if (body.isBlank()) content.addView(emptyState(if (detailTab == "notes") R.drawable.ic_notes else R.drawable.ic_transcript,
                if (detailTab == "notes") "아직 노트가 없어요" else "아직 전사가 없어요", "연결된 PC의 처리 결과를 확인해 주세요.", "다시 확인", !vemo().gate.busy()) { vemo().sync(true) })
            else if (detailTab == "notes") renderNotes(body)
            else renderTranscript(item, result)
        } else {
            when {
                syncProblem != null -> content.addView(syncProblemPanel(syncProblem, compact = false))
                vemo().syncing && vemo().syncFeedback.isAttempting(id) -> content.addView(emptyState(R.drawable.ic_cloud, "동기화를 확인하고 있어요", "기기 원본을 보존하며 업로드와 결과를 확인하고 있어요."))
                item.state == CaptureState.INTERRUPTED -> content.addView(emptyState(R.drawable.ic_warning, "녹음이 중단되었어요", state(item), "복구 시도", !vemo().gate.busy()) { vemo().recover(id) })
                item.state == CaptureState.FAILED -> content.addView(emptyState(R.drawable.ic_warning, "녹음을 완료하지 못했어요", state(item)))
                item.mode == Mode.DESKTOP && item.uploadable && item.uploaded -> content.addView(emptyState(R.drawable.ic_cloud,
                    "PC 결과를 기다리고 있어요", "녹음은 안전하게 저장되어 있어요.\n연결된 PC에서 처리가 끝나면 전사와 노트가 도착해요.", "다시 확인", !vemo().gate.busy()) { vemo().sync(true) })
                item.mode == Mode.DESKTOP && item.uploadable -> content.addView(emptyState(R.drawable.ic_cloud,
                    "업로드를 기다리고 있어요", "클라우드 연결 후 완료한 녹음을 업로드하세요.\n기기 원본은 오프라인에서도 유지됩니다.", if (vemo().cloudConfigured()) "다시 시도" else "연결 설정", !vemo().gate.busy()) {
                        if (vemo().cloudConfigured()) vemo().sync(true) else { stopPlayer(); tab = "settings"; detailId = null; render() }
                    })
                else -> content.addView(emptyState(R.drawable.ic_transcript, "아직 전사와 노트가 없어요",
                    if (item.mode == Mode.RECORD_ONLY) "녹음만 저장한 기록이에요.\n오디오를 듣거나 공유할 수 있어요." else "녹음이 완료되면 PC 처리 결과를 확인할 수 있어요."))
            }
        }
        // Keep diagnostics available without repeating a second status block below the designed state.
        if (item.status.isNotBlank()) {
            val diagnostic = text(item.status, 12f, muted).apply {
                visibility = View.GONE; setTextIsSelectable(true)
                setLineSpacing(dp(2).toFloat(), 1.2f)
            }
            content.addView(compactAction("상태 자세히", outlined = false, action = {}).apply {
                textSize = 12f
                setOnClickListener {
                    val open = diagnostic.visibility != View.VISIBLE
                    diagnostic.visibility = if (open) View.VISIBLE else View.GONE
                    text = if (open) "상태 접기" else "상태 자세히"
                }
            }, LinearLayout.LayoutParams(-2, -2).apply { gravity = Gravity.CENTER_HORIZONTAL })
            content.addView(diagnostic, LinearLayout.LayoutParams(-1, -2))
        }
    }

    private fun renderTranscript(item: Recording, result: Result) {
        val sentences = result.timeline ?: TranscriptTimeline.plain(result.transcript)
        val timestampPaint = text("", 12f, purple).paint
        val timestampWidth = maxOf(dp(48), (sentences.mapNotNull { it.timestamp }
            .maxOfOrNull { timestampPaint.measureText(it) } ?: 0f).toInt() + dp(12))
        for (sentence in sentences) {
            if (sentence.text.isBlank()) continue
            divider(content)
            val line = row().apply {
                gravity = Gravity.TOP
                setPadding(0, dp(16), 0, dp(16))
            }
            sentence.timestamp?.let { timestamp ->
                line.addView(text(timestamp, 12f, purple).apply {
                    gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
                    minWidth = dp(48); minHeight = dp(48)
                    setPadding(0, dp(4), dp(10), dp(4))
                    contentDescription = "$timestamp 위치로 이동"
                    isFocusable = true
                    isEnabled = !vemo().gate.isRecording()
                    background = android.graphics.drawable.RippleDrawable(ripple, null, surface(Color.WHITE))
                    setOnClickListener {
                        if (!vemo().gate.isRecording()) {
                            if (playerId != item.id) play(item)
                            // play() prepares synchronously; failed setup leaves no matching player.
                            if (playerId == item.id) seekPlayback(sentence.startMs!!)
                        }
                    }
                }, LinearLayout.LayoutParams(timestampWidth, -2))
            }
            line.addView(text(sentence.text.trim(), 16f).apply {
                setTextIsSelectable(true)
                setLineSpacing(dp(4).toFloat(), 1.3f)
            }, LinearLayout.LayoutParams(0, -2, 1f))
            content.addView(line, LinearLayout.LayoutParams(-1, -2))
        }
    }

    private fun compactAction(title: String, icon: Int? = null, outlined: Boolean = false, action: () -> Unit): Button = button(title, action = action).apply {
        minHeight = dp(48); textSize = 14f
        setPadding(dp(12), dp(8), dp(12), dp(8))
        setTextColor(muted)
        background = android.graphics.drawable.RippleDrawable(ripple, surface(if (outlined) subtle else Color.WHITE, outlined), null)
        if (icon != null) {
            val drawable = getDrawable(icon)?.mutate()?.apply { setTint(muted); setBounds(0, 0, dp(22), dp(22)) }
            setCompoundDrawablesRelative(drawable, null, null, null); compoundDrawablePadding = dp(6)
        }
    }
    private fun renderNotes(markdown: String) {
        // Only format explicit source headings/list markers; never infer sections or summaries.
        val styled = android.text.SpannableStringBuilder()
        markdown.lines().forEachIndexed { index, source ->
            if (index > 0) styled.append('\n')
            val heading = Regex("^(#{1,6}) +(.+)$").matchEntire(source)
            val bullet = Regex("^([ \t]*)[-*+] +(.+)$").matchEntire(source)
            val start = styled.length
            styled.append(heading?.groupValues?.get(2) ?: bullet?.let { it.groupValues[1] + it.groupValues[2] } ?: source)
            val end = styled.length
            if (heading != null) {
                styled.setSpan(android.text.style.StyleSpan(Typeface.BOLD), start, end, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                styled.setSpan(android.text.style.RelativeSizeSpan(if (heading.groupValues[1].length <= 2) 1.25f else 1.1f), start, end, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            }
            if (bullet != null) styled.setSpan(android.text.style.BulletSpan(dp(8)), start, end, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        }
        content.addView(text("", 16f).apply {
            text = styled; setTextIsSelectable(true); setLineSpacing(dp(5).toFloat(), 1.3f)
        })
    }
    private fun settingsGroup(title: String): LinearLayout {
        content.addView(text(title, 13f, muted).apply { setPadding(0, dp(18), 0, dp(10)) })
        return column().apply { background = surface(Color.WHITE, true); content.addView(this) }
    }
    private fun settingRow(target: LinearLayout, title: String, subtitle: String, actionTitle: String? = null, action: (() -> Unit)? = null): TextView {
        if (target.childCount > 0) divider(target)
        val summary = text(subtitle, 13f, muted).apply {
            setLineSpacing(dp(2).toFloat(), 1.1f)
            visibility = if (subtitle.isBlank()) View.GONE else View.VISIBLE
        }
        val body = column().apply { addView(text(title, 15f)); addView(summary) }
        target.addView(if (resources.configuration.fontScale > 1.3f) column().apply {
            setPadding(dp(14), dp(10), dp(14), dp(10)); addView(body)
            if (actionTitle != null && action != null) addView(compactAction(actionTitle, action = action))
        } else row().apply {
            setPadding(dp(14), dp(10), dp(10), dp(10))
            addView(body, LinearLayout.LayoutParams(0, -2, 1f))
            if (actionTitle != null && action != null) addView(compactAction(actionTitle, action = action), LinearLayout.LayoutParams(-2, -2))
        })
        return summary
    }
    private fun configureSection(title: String, build: () -> Unit): () -> Unit {
        val host = content
        val body = column().apply { visibility = View.GONE; setPadding(dp(14), dp(8), dp(14), dp(14)); background = surface(Color.WHITE, true) }
        val toggle = compactAction("$title  ▾", action = {}).apply {
            gravity = Gravity.START or Gravity.CENTER_VERTICAL
            if (Build.VERSION.SDK_INT >= 28) isAccessibilityHeading = true
            setOnClickListener {
                val open = body.visibility != View.VISIBLE
                body.visibility = if (open) View.VISIBLE else View.GONE
                text = "$title  " + if (open) "▴" else "▾"
                contentDescription = title + if (open) ", 펼쳐짐" else ", 접힘"
            }
            contentDescription = "$title, 접힘"
        }
        host.addView(toggle)
        host.addView(body)
        content = body
        try { build() } finally { content = host }
        return {
            body.visibility = View.VISIBLE; toggle.text = "$title  ▴"; toggle.contentDescription = "$title, 펼쳐짐"
            toggle.post { toggle.requestRectangleOnScreen(android.graphics.Rect(0, 0, toggle.width, toggle.height), false) }
        }
    }
    private fun settings() {
        val mode = settingsGroup("녹음")
        settingRow(mode, "기본 모드", if (vemo().mode() == Mode.DESKTOP) "PC 자동 처리" else "녹음만", "변경") {
            val choices = arrayOf("PC 자동 처리 · 클라우드 연결 시", "녹음만 · 폴더 저장/공유")
            choiceDialog("기본 녹음 모드", choices.toList(), if (vemo().mode() == Mode.DESKTOP) 0 else 1) { which ->
                vemo().preferences.edit().putString("mode", if (which == 0) "desktop" else "record_only").apply()
                render()
            }
        }
        val storage = settingsGroup("저장 위치")
        val folderChosen = vemo().preferences.getString("folder", null) != null
        storage.addView(Switch(this).apply {
            text = "저장 폴더에 자동 복사"; textSize = 15f; setTextColor(ink)
            minimumHeight = dp(56); setPadding(dp(14), dp(10), dp(14), dp(10))
            isChecked = vemo().preferences.getBoolean("auto_copy", true)
            contentDescription = "저장 폴더에 자동 복사, 완료한 녹음에 적용"
            setOnCheckedChangeListener { _, enabled -> vemo().preferences.edit().putBoolean("auto_copy", enabled).apply() }
        }, LinearLayout.LayoutParams(-1, -2))
        settingRow(storage, "녹음 폴더", if (folderChosen) "시스템 폴더 선택됨 · 원본은 기기에 보관" else "선택한 폴더 없음", if (folderChosen) "변경" else "선택") {
            try { startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION), 51) }
            catch (error: Exception) { vemo().notify(safeError(error)) }
        }
        if (folderChosen) settingRow(storage, "저장 폴더 연결", "자동 복사 끄기는 폴더 선택을 유지합니다.", "연결 해제") {
            vemo().preferences.getString("folder", null)?.let { try { contentResolver.releasePersistableUriPermission(Uri.parse(it), Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) } catch (_: Exception) { } }
            vemo().preferences.edit().remove("folder").apply(); render()
        }
        val connections = settingsGroup("연결")
        var openGoogle: () -> Unit = {}
        var openDav: () -> Unit = {}
        val googleSummary = settingRow(connections, "Google Drive", "연결 상태 확인 중", "구성") { openGoogle() }
        val davSummary = settingRow(connections, "Nextcloud", "연결 상태 확인 중", "구성") { openDav() }
        val processorSummary = settingRow(connections, "PC 처리기", "동기화하여 연결 상태와 결과 확인", "확인") { vemo().sync(true) }
        val cancelRow = column()
        connections.addView(cancelRow)
        settingRow(cancelRow, "Drive 동기화", "취소 후 다시 시도할 수 있습니다.", "취소") {
            vemo().google.cancel(); vemo().notify("Drive 동기화 취소 요청 · 다시 시도할 수 있습니다.")
        }
        cancelRow.addView(View(this).apply { setBackgroundColor(line) }, 0, LinearLayout.LayoutParams(-1, dp(1)))
        refreshSettings = {
            val binding = try { vemo().google.binding() } catch (_: Exception) { null }
            val dav = try { vemo().vault.load() } catch (_: Exception) { null }
            val googleActive = vemo().preferences.getString("provider", "nextcloud") == "google"
            googleSummary.text = if (binding == null) "연결된 계정 없음" else if (googleActive) "연결됨 · 자동 동기화에 사용" else "연결 정보 저장됨"
            davSummary.text = if (dav == null) "연결된 계정 없음" else if (!googleActive) "연결 정보 저장됨 · 자동 동기화에 사용" else "연결 정보 저장됨"
            processorSummary.text = if (vemo().syncing) "동기화 중" else "동기화하여 연결 상태와 결과 확인"
            cancelRow.visibility = if (vemo().syncing && googleActive) View.VISIBLE else View.GONE
        }
        refreshSettings?.invoke()
        statusView = text(vemo().message, 13f, muted).apply { accessibilityLiveRegion = View.ACCESSIBILITY_LIVE_REGION_POLITE }
        content.addView(statusView)
        gap(8)
        openGoogle = configureSection("Google Drive 연결 설정 · 고급") { googleSettings() }
        openDav = configureSection("Nextcloud 연결 설정 · 고급") { nextcloudSettings() }
        gap(16)
        content.addView(text("기기 원본은 앱 내부에 보관합니다. 앱 삭제/데이터 초기화 전에는 폴더 복사 또는 공유로 내보내세요.", 13f, muted))
        content.addView(text("표시되는 폴더 공급자는 Android가 결정합니다. Google Drive가 폴더 선택에 표시되지 않으면 녹음의 공유 버튼에서 Drive를 선택하세요. 공유는 자동 PC 처리 연결과 별개입니다.", 13f, muted))
    }
    private fun nextcloudSettings() {
        content.addView(text("HTTPS WebDAV 폴더로 완료한 녹음과 PC 결과를 교환합니다. 데스크톱에서 같은 폴더를 등록하세요.", 14f, muted))
        val old = try { vemo().vault.load() } catch (_: Exception) { null }
        fun field(caption: String, initial: String = "", secret: Boolean = false): EditText {
            content.addView(text(caption, 13f, muted))
            val edit = EditText(this).apply {
                setText(initial); setSingleLine(true); textSize = 15f; minHeight = dp(48)
                isSaveEnabled = false; importantForAutofill = View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS
                setTextColor(ink); setHintTextColor(muted)
                setPadding(dp(16), dp(12), dp(16), dp(12)); background = surface(subtle)
                inputType = if (secret) InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD else InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
            }
            content.addView(edit, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(12) })
            return edit
        }
        val endpoint = field("WebDAV URL (https://cloud.example/remote.php/dav/files/사용자)", old?.endpoint ?: "")
        val folder = field("상대 폴더 경로", old?.folder ?: "Vemo")
        val user = field("사용자 이름", old?.user ?: "")
        val password = field(if (old == null) "앱 비밀번호" else "앱 비밀번호 · 빈칸이면 저장된 값 유지", secret = true)
        content.addView(button("연결 저장 및 처리기 확인", true) {
            if (!vemo().gate.beginSync()) { vemo().notify("녹음/동기화가 끝난 뒤 연결을 변경하세요."); return@button }
            val credentials = DavCredentials(endpoint.text.toString().trim().trimEnd('/'), folder.text.toString().trim(), user.text.toString().trim(), password.text.toString().ifEmpty { old?.password ?: "" })
            password.text.clear()
            vemo().io.execute {
                try {
                    DavConfig(credentials.endpoint, credentials.folder).validate()
                    if (credentials.password.isEmpty() || credentials.user.isBlank()) throw ProtocolException("사용자 이름과 앱 비밀번호를 입력하세요.")
                    // Read-only connectivity check. Missing processor is an explicit setup state, not success.
                    val cloud = WebDavCloud(DavConfig(credentials.endpoint, credentials.folder), credentials.user, credentials.password)
                    val bytes = cloud.read("processor.json", Wire.MAX_JSON)
                    val processor = bytes?.let { Wire.processor(it) }
                    vemo().vault.save(credentials)
                    vemo().preferences.edit().putString("provider", "nextcloud").apply()
                    vemo().message = if (processor == null) "로그인 정보를 보호 저장했습니다. 선택한 폴더에 processor.json이 없습니다. PC에서 먼저 등록하세요." else "Nextcloud 연결됨 · ${processor.displayName}"
                    SyncJobService.schedule(this)
                } catch (error: Exception) { vemo().message = safeError(error) }
                finally { vemo().gate.endSync(); vemo().changed() }
            }
        })
        content.addView(button("Nextcloud 연결 해제") {
            if (vemo().gate.busy()) { vemo().notify("진행 중 작업이 끝나면 연결을 해제하세요."); return@button }
            try { vemo().vault.clear(); getSystemService(android.app.job.JobScheduler::class.java).cancel(8); vemo().notify("Nextcloud 연결을 해제했습니다. 기기 원본은 보존했습니다."); render() }
            catch (error: Exception) { vemo().notify(safeError(error)) }
        })
        content.addView(text("비밀번호는 Android Keystore로 암호화합니다. TLS 인증서 검증과 콘텐츠 충돌 보호를 사용합니다.", 13f, muted))
    }
    private fun googleSettings() {
        label("Google Drive · 선택 사항", 20f)
        val binding = try { vemo().google.binding() } catch (_: Exception) { null }
        content.addView(text(if (binding == null) "Mac 또는 Windows Vemo에서 먼저 Google로 연결한 뒤, 같은 계정의 폴더를 선택하세요." else "Google 계정 ID: ${binding.accountId}\n연결된 Drive root: ${binding.rootId}\n처리기: ${binding.processorId}", 14f, muted))
        content.addView(text("버튼을 눌러 Google 계정으로 로그인하고 Vemo 파일 접근을 승인하세요. 별도 설정 파일은 필요하지 않습니다.", 13f, muted))
        content.addView(button(if (binding == null) "Google로 연결" else "같은 계정으로 다시 승인", true) {
            if (!vemo().gate.beginSync()) { vemo().notify("녹음/동기화가 끝난 뒤 연결하세요."); return@button }
            Identity.getAuthorizationClient(this).authorize(vemo().google.request())
                .addOnSuccessListener { result ->
                    vemo().gate.endSync()
                    try {
                        if (result.hasResolution()) {
                            if (isFinishing || isDestroyed || !hasWindowFocus()) throw AuthException()
                            startIntentSenderForResult(result.pendingIntent!!.intentSender, 61, null, 0, 0, 0)
                        } else discoverGoogle(result.accessToken ?: throw AuthException())
                    } catch (_: Exception) { vemo().notify("Google 승인 화면을 열 수 없습니다. 다시 시도하세요.") }
                }.addOnFailureListener { _ ->
                    vemo().gate.endSync()
                    vemo().notify("Google 로그인에 실패했습니다. 네트워크를 확인하고 다시 시도하세요. 계속되면 최신 Vemo로 업데이트한 뒤 지원팀에 문의하세요.")
                }
        })
        if (binding != null) {
            content.addView(button("이 Google Drive 연결 사용") {
                if (vemo().gate.busy()) { vemo().notify("진행 중 작업을 마친 뒤 변경하세요."); return@button }
                vemo().preferences.edit().putString("provider", "google").apply()
                vemo().notify("Google Drive 연결을 선택했습니다."); SyncJobService.schedule(this)
            })
            content.addView(button("Google Drive 연결 해제") {
                if (vemo().gate.busy()) { vemo().notify("진행 중 작업을 마친 뒤 해제하세요."); return@button }
                try {
                    vemo().google.disconnect()
                    getSystemService(android.app.job.JobScheduler::class.java).cancel(8)
                    vemo().notify("이 기기의 Google Drive 연결을 해제했습니다. 원본과 녹음별 이전 연결은 보존했습니다.")
                    render()
                } catch (error: Exception) { vemo().notify(safeError(error)) }
            })
        }
    }
    private fun discoverGoogle(token: String) {
        if (!vemo().gate.beginSync()) { vemo().notify("진행 중 작업 후 다시 승인하세요."); return }
        vemo().google.accept(token)
        vemo().io.execute {
            try {
                val api = vemo().google.api()
                val account = api.accountId()
                val roots = api.discoverRoots()
                runOnUiThread {
                    if (!isFinishing && !isDestroyed && hasWindowFocus()) {
                        if (roots.isEmpty()) vemo().notify("연결할 Vemo 폴더가 없습니다. 같은 Google 계정으로 Mac 또는 Windows Vemo에서 먼저 연결하세요.")
                        else choiceDialog("PC 처리기 폴더 선택", roots.map { "${it.name} · ${it.processorId} · ${it.id}" }) { index ->
                            selectGoogle(DriveBinding(account, roots[index].id, roots[index].processorId))
                        }
                    } else vemo().notify("설정에서 Google 연결을 다시 눌러 폴더를 선택하세요.")
                }
            } catch (error: Exception) {
                if (error is AuthException) vemo().google.invalidate()
                vemo().notify(safeError(error))
            } finally { vemo().gate.endSync() }
        }
    }
    private fun selectGoogle(binding: DriveBinding) {
        if (!vemo().gate.beginSync()) { vemo().notify("진행 중 작업 후 다시 연결하세요."); return }
        vemo().io.execute {
            try {
                vemo().google.select(binding)
                vemo().preferences.edit().putString("provider", "google").apply()
                vemo().notify("Google Drive 연결됨 · 기존 녹음의 계정/폴더/처리기 연결은 보존합니다.")
                SyncJobService.schedule(this)
                runOnUiThread { if (!isFinishing && !isDestroyed) render() }
            } catch (error: Exception) { vemo().notify(safeError(error)) }
            finally { vemo().gate.endSync() }
        }
    }
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == 61) {
            if (resultCode != RESULT_OK) { vemo().notify("Google 승인을 취소했습니다. 기존 연결은 보존했습니다."); return }
            try {
                val result = Identity.getAuthorizationClient(this).getAuthorizationResultFromIntent(data)
                val token = result.accessToken ?: throw AuthException()
                discoverGoogle(token)
            } catch (_: Exception) { vemo().notify("Google 승인을 완료하지 못했습니다. 설정에서 다시 연결하세요.") }
            return
        }
        if (requestCode == 51 && resultCode == RESULT_OK) {
            val uri = data?.data ?: return
            try {
                val flags = data.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
                if (flags and Intent.FLAG_GRANT_WRITE_URI_PERMISSION == 0) throw SecurityException()
                if (flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0) contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
                else throw SecurityException()
                vemo().preferences.edit().putString("folder", uri.toString()).apply()
                vemo().notify("저장 폴더를 선택했습니다. 기기 원본을 먼저 확정한 뒤 복사합니다.")
                render()
            } catch (error: Exception) { vemo().notify(safeError(error)) }
        }
    }
    private fun playbackPanel(): LinearLayout = column().apply {
        background = surface(subtle, true)
        setPadding(dp(8), dp(8), dp(8), dp(8))
        playbackSeek = SeekBar(this@MainActivity).apply {
            minimumHeight = dp(56); max = player?.duration ?: 0
            fun track(color: Int) = GradientDrawable().apply {
                setColor(color); cornerRadius = dp(4).toFloat(); setSize(dp(8), dp(8))
            }
            progressDrawable = android.graphics.drawable.LayerDrawable(arrayOf(
                track(selection),
                android.graphics.drawable.ClipDrawable(track(purple), Gravity.START, android.graphics.drawable.ClipDrawable.HORIZONTAL)
            )).apply {
                setId(0, android.R.id.background); setId(1, android.R.id.progress)
                for (layer in 0..1) {
                    setLayerHeight(layer, dp(8)); setLayerGravity(layer, Gravity.CENTER_VERTICAL)
                }
            }
            progressBackgroundTintList = android.content.res.ColorStateList.valueOf(selection)
            progressTintList = android.content.res.ColorStateList.valueOf(purple)
            thumb = GradientDrawable().apply {
                shape = GradientDrawable.OVAL; setColor(purple); setSize(dp(12), dp(12))
            }
            thumbTintList = android.content.res.ColorStateList.valueOf(purple)
            thumbOffset = dp(6); splitTrack = false
            setPadding(dp(6), 0, dp(6), 0)
            contentDescription = "재생 위치"
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onStartTrackingTouch(seekBar: SeekBar) { playbackDragging = true }
                override fun onProgressChanged(seekBar: SeekBar, progress: Int, fromUser: Boolean) {
                    if (fromUser) {
                        playbackTime?.text = "${duration(progress.toLong())} / ${duration(seekBar.max.toLong())}"
                        if (!playbackDragging) seekPlayback(progress.toLong())
                    }
                }
                override fun onStopTrackingTouch(seekBar: SeekBar) {
                    playbackDragging = false
                    seekPlayback(seekBar.progress.toLong())
                    flushPlaybackRefresh()
                }
            })
            setOnTouchListener { view, event ->
                if (event.actionMasked == MotionEvent.ACTION_CANCEL) view.post {
                    if (playbackSeek === view) {
                        playbackDragging = false
                        updatePlaybackControls()
                        flushPlaybackRefresh()
                    }
                }
                false // Keep native SeekBar drag, keyboard and accessibility handling.
            }
        }
        addView(playbackSeek, LinearLayout.LayoutParams(-1, -2))
        playbackTime = text("", 13f, muted).apply { gravity = Gravity.CENTER }
        addView(playbackTime)
        fun transport(icon: Int, description: String, iconSize: Int = 18, action: () -> Unit) =
            headerAction(icon, description, action).apply {
                val inset = dp((48 - iconSize) / 2)
                setPadding(inset, inset, inset, inset)
                scaleType = ImageView.ScaleType.FIT_CENTER
                background = android.graphics.drawable.RippleDrawable(ripple, null, surface(Color.WHITE))
            }
        addView(row().apply {
            gravity = Gravity.CENTER
            addView(transport(R.drawable.ic_replay_10, "10초 뒤로") { skipPlayback(-10_000) })
            playbackToggle = transport(R.drawable.ic_pause, "일시정지", 20) { togglePlayback() }
            addView(playbackToggle)
            addView(transport(R.drawable.ic_forward_10, "10초 앞으로") { skipPlayback(10_000) })
        })
        updatePlaybackControls()
        this@MainActivity.handler.post(playbackTick)
        if (revealPlayer) {
            revealPlayer = false
            if (android.animation.ValueAnimator.areAnimatorsEnabled()) {
                alpha = 0f; translationY = dp(8).toFloat()
                animate().alpha(1f).translationY(0f).setDuration(180).start()
            }
        }
    }
    private fun flushPlaybackRefresh() {
        if (playbackRefreshPending && !playbackDragging) {
            playbackRefreshPending = false
            if (activityResumed) render()
        }
    }
    private fun updatePlaybackControls() {
        val media = player ?: return
        try {
            val total = media.duration.coerceAtLeast(0)
            val position = (pendingSeek ?: if (playbackComplete) total else media.currentPosition).coerceIn(0, total)
            if (!playbackDragging) {
                playbackSeek?.max = total; playbackSeek?.progress = position
                playbackTime?.text = "${duration(position.toLong())} / ${duration(total.toLong())}"
            }
            playbackToggle?.apply {
                setImageResource(if (media.isPlaying) R.drawable.ic_pause else R.drawable.ic_play)
                contentDescription = if (media.isPlaying) "일시정지" else if (playbackComplete) "처음부터 다시 재생" else "재생"
                tooltipText = contentDescription
            }
        } catch (error: Exception) { playbackError(error) }
    }
    private fun skipPlayback(deltaMs: Long) {
        val media = player ?: return
        try { seekPlayback((pendingSeek ?: if (playbackComplete) media.duration else media.currentPosition).toLong() + deltaMs) }
        catch (error: Exception) { playbackError(error) }
    }
    private fun seekPlayback(positionMs: Long) {
        val media = player ?: return
        try {
            val target = positionMs.coerceIn(0L, media.duration.toLong()).toInt()
            pendingSeek = target; playbackComplete = false
            if (seekInFlight == null) issuePlaybackSeek(media, target)
            updatePlaybackControls()
        } catch (error: Exception) { playbackError(error) }
    }
    private fun issuePlaybackSeek(media: MediaPlayer, target: Int) {
        seekInFlight = target
        media.seekTo(target.toLong(), MediaPlayer.SEEK_CLOSEST)
    }
    private fun togglePlayback() {
        val media = player ?: return
        try {
            if (media.isPlaying) media.pause() else {
                if (playbackComplete || (pendingSeek ?: media.currentPosition) >= media.duration) seekPlayback(0)
                media.start()
            }
            updatePlaybackControls()
        } catch (error: Exception) { playbackError(error) }
    }
    private fun play(item: Recording) {
        if (playerId == item.id) return
        stopPlayer()
        try {
            val media = MediaPlayer()
            player = media // Own the instance before setup so every preparation failure releases it.
            media.setDataSource(vemo().store.audio(item.id).absolutePath)
            media.setOnCompletionListener { completed ->
                if (player === completed && pendingSeek == null) {
                    playbackComplete = true; updatePlaybackControls()
                }
            }
            media.setOnSeekCompleteListener { completed ->
                if (player === completed) {
                    val finishedTarget = seekInFlight
                    seekInFlight = null
                    val desired = pendingSeek
                    try {
                        if (desired != null && desired != finishedTarget) issuePlaybackSeek(completed, desired)
                        else pendingSeek = null
                        updatePlaybackControls()
                    } catch (error: Exception) { playbackError(error) }
                }
            }
            media.setOnErrorListener { failed, _, _ ->
                if (player === failed) {
                    stopPlayer(); vemo().notify("오디오를 재생할 수 없습니다. 원본은 보존했습니다.")
                }
                true
            }
            media.prepare(); media.start()
            playerId = item.id; revealPlayer = true; render()
        } catch (error: Exception) { playbackError(error) }
    }
    private fun playbackError(error: Exception) { stopPlayer(); vemo().notify(safeError(error)) }
    private fun stopPlayer() {
        handler.removeCallbacks(playbackTick)
        val released = player
        player = null; playerId = null; pendingSeek = null; seekInFlight = null; playbackComplete = false; revealPlayer = false
        try { released?.release() } catch (_: Exception) { }
        playbackDragging = false
        flushPlaybackRefresh()
    }
    private fun duration(ms: Long): String = "%02d:%02d".format(ms / 60_000, ms / 1000 % 60)
    private fun date(value: String): String = try { OffsetDateTime.parse(value).format(DateTimeFormatter.ofPattern("yyyy.MM.dd HH:mm xxx")) } catch (_: Exception) { value }
    private fun state(item: Recording): String = when (item.state) {
        CaptureState.RECORDING -> "녹음 중"
        CaptureState.PAUSED -> "일시정지 · 업로드 안 함"
        CaptureState.FINALIZING -> "원본 확정 중"
        CaptureState.COMPLETE -> "기기 원본 저장됨" + if (item.exported) " · 폴더 복사됨" else ""
        CaptureState.INTERRUPTED -> "중단됨 · 원본 보존/복구 필요"
        CaptureState.FAILED -> "녹음 실패 · 보존한 원본 확인 필요"
    }
}
