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
    private val purple = Color.rgb(98, 88, 238)
    private val deleteRed = Color.rgb(201, 60, 72)
    private val ink = Color.rgb(34, 35, 44)
    private val muted = Color.rgb(110, 110, 122)
    private val background = Color.WHITE
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
    private var syncAction: Button? = null
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
    private var detailId: String? = null
    private var timer: TextView? = null
    private var statusView: TextView? = null
    private var player: MediaPlayer? = null
    private var playerId: String? = null
    private val handler = Handler(Looper.getMainLooper())
    private val listener: () -> Unit = {
        statusView?.text = vemo().message
        if (vemo().message != lastNotice) {
            lastNotice = vemo().message
            if (lastNotice.isNotBlank()) Toast.makeText(this, lastNotice, Toast.LENGTH_LONG).show()
        }
        // Keep the search field and keyboard intact when background sync changes rows.
        if (interactionFrozen) { refreshPending = true }
        else if (tab == "list" && detailId == null && libraryStructure() == lastStructure) {
            updateEntries(); updateRecordingAction(); updateSyncAction()
        } else if (tab != "settings") render()
    }
    private val tick = object : Runnable {
        override fun run() {
            timer?.text = duration(vemo().elapsedMs)
            updateRecordingAction()
            handler.postDelayed(this, 1000)
        }
    }
    private val poll = object : Runnable {
        override fun run() {
            if (!vemo().gate.busy() && vemo().cloudConfigured() && vemo().store.list().any { it.mode == Mode.DESKTOP && it.uploaded && vemo().store.result(it) == null }) vemo().sync()
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
    override fun onResume() { super.onResume(); vemo().listeners.add(listener); handler.post(tick); handler.postDelayed(poll, 30_000); SyncJobService.schedule(this); if (!::container.isInitialized) render() else listener() }
    override fun onPause() { if (interactionFrozen) container.cancelDragAndDrop(); holds.forEach { it.cancel() }; vemo().listeners.remove(listener); handler.removeCallbacks(tick); handler.removeCallbacks(poll); stopPlayer(); super.onPause() }
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
        setColor(color); cornerRadius = dp(16).toFloat()
        if (border) setStroke(dp(1), Color.rgb(235, 235, 240))
    }
    private fun button(label: String, primary: Boolean = false, action: () -> Unit): Button = Button(this).apply {
        text = label; isAllCaps = false; textSize = 15f; minHeight = dp(52)
        minimumWidth = 0; minWidth = 0
        stateListAnimator = null; elevation = 0f
        setPadding(dp(16), dp(12), dp(16), dp(12))
        setTextColor(if (primary) Color.WHITE else purple)
        backgroundTintList = null
        background = android.graphics.drawable.RippleDrawable(
            android.content.res.ColorStateList.valueOf(0x226258EE),
            surface(if (primary) purple else Color.rgb(245, 245, 248)), null)
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(4); bottomMargin = dp(4) }
        setOnClickListener { action() }
    }
    private fun gap(height: Int = 12) { content.addView(Space(this), LinearLayout.LayoutParams(1, dp(height))) }
    private fun label(value: String) { content.addView(text(value, 26f, ink, true)) }
    private fun card(): LinearLayout = column().apply {
        setPadding(dp(18), dp(20), dp(18), dp(20))
        background = surface(Color.rgb(248, 248, 250))
    }
    private fun divider(target: LinearLayout) {
        target.addView(View(this).apply { setBackgroundColor(Color.rgb(239, 239, 243)) }, LinearLayout.LayoutParams(-1, dp(1)))
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
            CaptureState.RECORDING -> "● 녹음 중 · ${duration(vemo().elapsedMs)} · 열기"
            CaptureState.PAUSED -> "Ⅱ 일시정지 · ${duration(vemo().elapsedMs)} · 열기"
            CaptureState.FINALIZING -> "녹음 저장 중 · 열기"
            else -> "＋ 새 녹음"
        }
    }
    private fun updateSyncAction() {
        syncAction?.apply {
            text = if (vemo().syncing) "동기화 중…" else "녹음 업로드 / 결과 확인"
            isEnabled = !vemo().gate.busy()
        }
    }
    private fun render() {
        if (interactionFrozen) { refreshPending = true; return }
        holds.forEach { it.cancel() }; holds.clear()
        timer = null; search = null; entries = null; recordingAction = null; syncAction = null; folderControls.clear()
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
        container.addView(row().apply {
            if (tab != "list" || detailId != null) addView(ImageButton(this@MainActivity).apply {
                setImageResource(R.drawable.ic_back); imageTintList = android.content.res.ColorStateList.valueOf(ink)
                contentDescription = "라이브러리로 돌아가기"
                background = android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x226258EE), surface(Color.WHITE), null)
                setPadding(dp(12), dp(12), dp(12), dp(12)); stateListAnimator = null; elevation = 0f
                setOnClickListener { home() }
            }, LinearLayout.LayoutParams(dp(48), dp(48)))
            addView(ImageView(this@MainActivity).apply {
                setImageResource(R.drawable.vemo_icon); importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, LinearLayout.LayoutParams(dp(32), dp(32)).apply { marginEnd = dp(10) })
            addView(text("Vemo", 23f, ink, true), LinearLayout.LayoutParams(0, -2, 1f))
            addView(ImageButton(this@MainActivity).apply {
                setImageResource(R.drawable.ic_settings); imageTintList = android.content.res.ColorStateList.valueOf(muted)
                contentDescription = "설정"; background = surface(Color.WHITE)
                setPadding(dp(12), dp(12), dp(12), dp(12))
                setOnClickListener { stopPlayer(); tab = "settings"; detailId = null; render() }
            }, LinearLayout.LayoutParams(dp(48), dp(48)))
        })
        divider(container)
        val scroll = LibraryDragScroll(this) { finishInteraction() }.apply { isFillViewport = true }
        mainScroll = scroll
        content = column().apply { val padding = if (tab == "list" && detailId == null && compactLibrary()) 4 else 16; setPadding(0, dp(padding), 0, dp(padding)) }
        scroll.addView(content)
        container.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        divider(container)
        if (tab != "record") {
            recordingAction = button("＋ 새 녹음", true) { stopPlayer(); detailId = null; tab = "record"; render() }
            container.addView(recordingAction)
            updateRecordingAction()
        }
        setContentView(container)
        container.requestApplyInsets()
        statusView = null
        when (tab) { "settings" -> settings(); "list" -> if (detailId == null) recordings() else detail(detailId!!); else -> record() }
    }
    private fun record() {
        val active = vemo().store.list().firstOrNull { it.state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING) }
        label(if (active == null) "새로운 녹음" else if (active.state == CaptureState.PAUSED) "잠시 쉬는 중" else if (active.state == CaptureState.FINALIZING) "녹음 저장 중" else "지금 녹음 중")
        content.addView(text(if (active == null) "저장 위치 · ${if (selectedFolder == null) "미분류" else folderTitle()}" else active.title, 14f, muted))
        gap()
        content.addView(card().apply {
            addView(text("●", 64f, purple).apply { gravity = Gravity.CENTER })
            timer = text(if (active == null) "00:00" else duration(vemo().elapsedMs), 48f, ink, true).apply { gravity = Gravity.CENTER }
            addView(timer)
            addView(text(if (active == null) "준비되면 녹음을 시작하세요." else if (active.state == CaptureState.PAUSED) "일시정지됨" else if (active.state == CaptureState.FINALIZING) "녹음 저장 중" else "녹음 중", 14f, muted).apply { gravity = Gravity.CENTER })
        })
        gap(20)
        if (active == null) {
            content.addView(button("녹음 시작", true) { startRecording() }.apply { isEnabled = !vemo().gate.busy() })
            content.addView(text("화면을 나가도 녹음은 계속됩니다.", 13f, muted))
        } else {
            if (active.state != CaptureState.FINALIZING) {
                content.addView(button(if (active.state == CaptureState.PAUSED) "녹음 재개" else "일시정지") { command(if (active.state == CaptureState.PAUSED) "RESUME" else "PAUSE") })
                content.addView(button("녹음 완료", true) { command("STOP") })
            }
        }
        gap()
        content.addView(button("녹음 목록 보기") { tab = "list"; detailId = null; render() })
        if (vemo().store.unreadableCount() > 0) content.addView(text("읽을 수 없는 로컬 메타데이터가 있습니다. 앱을 삭제하지 마세요. 원본 폴더는 보존했습니다.", 13f, Color.RED))
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
    private fun compactLibrary() = resources.configuration.screenHeightDp <= 480 && resources.configuration.screenWidthDp >= 600
    private fun recordings() {
        val compact = compactLibrary()
        if (!compact) label(folderTitle())
        search = EditText(this).apply {
            hint = "녹음 검색"; contentDescription = "녹음 검색"; textSize = 15f
            inputType = InputType.TYPE_CLASS_TEXT; setSingleLine(true)
            minHeight = dp(52); setPadding(dp(16), dp(12), dp(16), dp(12))
            background = surface(Color.rgb(245, 245, 248)); setTextColor(ink); setHintTextColor(muted)
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
            background = surface(Color.rgb(245, 245, 248))
            setPadding(dp(14), 0, dp(4), 0)
            addView(ImageView(this@MainActivity).apply {
                setImageResource(R.drawable.ic_search); imageTintList = android.content.res.ColorStateList.valueOf(muted)
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, LinearLayout.LayoutParams(dp(20), dp(20)))
            search?.background = null
            addView(search, LinearLayout.LayoutParams(0, -2, 1f))
        }
        if (compact) {
            content.addView(row().apply {
                addView(text(folderTitle(), 20f, ink, true).apply {
                    maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
                }, LinearLayout.LayoutParams(dp(220), -2).apply { marginEnd = dp(16) })
                addView(searchBar, LinearLayout.LayoutParams(0, -2, 1f))
            }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(4) })
        } else content.addView(searchBar, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(10); bottomMargin = dp(18) })
        libraryError?.let { content.addView(text(it, 14f, Color.rgb(160, 60, 45))) }
        if (vemo().store.unreadableCount() > 0) content.addView(text("읽을 수 없는 녹음 메타데이터가 있습니다. 원본은 보존했습니다. 앱을 삭제하지 마세요.", 14f, muted))
        val body = if (resources.configuration.screenWidthDp >= 600) row().apply { gravity = Gravity.TOP } else column()
        val navigation = column()
        navigation.addView(row().apply {
            addView(text("라이브러리", 13f, muted, true), LinearLayout.LayoutParams(0, -2, 1f))
            addView(button("＋ 폴더") { folderEditor(null, selectedFolder) }.apply {
                minHeight = dp(48)
                background = android.graphics.drawable.InsetDrawable(
                    android.graphics.drawable.RippleDrawable(
                        android.content.res.ColorStateList.valueOf(0x226258EE),
                        surface(Color.rgb(238, 236, 255)), null),
                    0, dp(6), 0, dp(6))
                setPadding(dp(16), dp(8), dp(16), dp(8))
            }, LinearLayout.LayoutParams(-2, -2))
        })
        val snapshot = library()
        fun filter(name: String, id: String?, onlyUnfiled: Boolean, count: Int, depth: Int = 0) {
            val selected = selectedFolder == id && unfiled == onlyUnfiled
            val color = if (selected) Color.rgb(85, 75, 214) else ink
            val control = row().apply {
                minimumHeight = dp(48)
                isSelected = selected; isClickable = true; isFocusable = true
                val fill = if (selected) Color.rgb(240, 239, 255) else Color.WHITE
                background = android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x226258EE), surface(fill), null)
                setPadding(dp(12 + depth.coerceAtMost(4) * 14), dp(8), dp(10), dp(8))
                setOnClickListener { selectedFolder = id; unfiled = onlyUnfiled; render() }
            }
            val icon = if (id != null) R.drawable.ic_folder else if (onlyUnfiled) R.drawable.ic_inbox else R.drawable.ic_library
            control.addView(ImageView(this).apply {
                setImageResource(icon); imageTintList = android.content.res.ColorStateList.valueOf(color)
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, LinearLayout.LayoutParams(dp(20), dp(20)).apply { marginEnd = dp(12) })
            val nameView = text(name, 15f, color, selected).apply { importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO }
            control.addView(nameView, LinearLayout.LayoutParams(0, -2, 1f))
            val countView = text(java.text.NumberFormat.getIntegerInstance().format(count), 12f, muted).apply { importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO }
            control.addView(countView, LinearLayout.LayoutParams(-2, -2).apply { marginStart = dp(8); marginEnd = dp(8) })
            control.addView(ImageView(this).apply {
                setImageResource(if (selected) R.drawable.ic_check else R.drawable.ic_chevron)
                imageTintList = android.content.res.ColorStateList.valueOf(if (selected) color else muted)
                importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
            }, LinearLayout.LayoutParams(dp(16), dp(16)))
            control.tag = depth
            if (id != null) {
                holds.add(LibraryHold(control, LibraryDrag(id, true), ::freezeInteraction) { folderActions(id) })
                dropTarget(control, id)
            }
            folderControls.add(FolderControl(control, nameView, countView, id, onlyUnfiled))
            navigation.addView(control, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(2) })
        }
        val items = vemo().store.list()
        fun standardFilters() {
            filter("전체 녹음", null, false, items.size)
            filter("미분류", null, true, items.count { snapshot?.recordingFolders?.get(it.id) == null })
        }
        if (!compact) {
            standardFilters()
            navigation.addView(text("폴더", 13f, muted, true))
        }
        val children = snapshot?.folders.orEmpty().groupBy { it.parentId }
        val pending = java.util.ArrayDeque<Pair<LibraryFolder, Int>>()
        children[null].orEmpty().sortedByDescending { it.name }.forEach { pending.addLast(it to 0) }
        val visited = mutableSetOf<String>()
        // Validated metadata is a tree; the visited set also protects rendering against cycles.
        while (pending.isNotEmpty() && visited.size < snapshot?.folders.orEmpty().size) {
            val (folder, depth) = pending.removeLast()
            if (!visited.add(folder.id)) continue
            filter(folder.name, folder.id, false, items.count { snapshot?.recordingFolders?.get(it.id) == folder.id }, depth)
            children[folder.id].orEmpty().sortedByDescending { it.name }.forEach { pending.addLast(it to depth + 1) }
        }
        if (compact) standardFilters()
        if (snapshot != null && snapshot.folders.isEmpty()) navigation.addView(text("폴더를 만들어 녹음을 정리하세요.", 14f, muted))
        val list = column()
        list.addView(text(if (selectedFolder == null && !unfiled) "최근 녹음" else "폴더의 녹음", 14f, muted, true))
        entries = column(); list.addView(entries)
        if (vemo().cloudConfigured()) {
            syncAction = button("녹음 업로드 / 결과 확인") { vemo().sync(true) }
            list.addView(syncAction)
            updateSyncAction()
        }
        if (resources.configuration.screenWidthDp >= 600) {
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
            target.addView(row().apply {
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(10), dp(12), dp(10)); minimumHeight = dp(60)
                addView(text(title, 17f, ink, true).apply {
                    maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
                }, LinearLayout.LayoutParams(0, -2, 1f))
                val recordedDate = try {
                    OffsetDateTime.parse(item.recordedAt).format(DateTimeFormatter.ofPattern("yyyy.MM.dd"))
                } catch (_: Exception) { item.recordedAt.substringBefore('T') }
                addView(text(recordedDate, 12f, muted).apply {
                    setSingleLine(true); gravity = Gravity.END
                }, LinearLayout.LayoutParams(-2, -2).apply { marginStart = dp(12) })
                isClickable = true; isFocusable = true
                background = android.graphics.drawable.RippleDrawable(
                    android.content.res.ColorStateList.valueOf(0x226258EE),
                    surface(Color.rgb(250, 250, 252), true), null)
                contentDescription = "$title, $recordedDate, ${duration(item.durationMs)}, ${state(item)}, 열기"
                setOnClickListener { openRecording(item.id) }
                for (index in 0 until childCount) getChildAt(index).importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
                holds.add(LibraryHold(this, LibraryDrag(item.id, false), ::freezeInteraction) { recordingActions(item.id) })
            }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(6); bottomMargin = dp(2) })
        }
        if (shown == 0) target.addView(text(if (query.isNotBlank()) "검색 결과가 없습니다." else "아직 녹음이 없습니다. 새 녹음을 시작하세요.", 16f, muted).apply {
            setPadding(0, dp(28), 0, dp(28))
        })
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
                    if (accepted) view.background = surface(Color.rgb(232, 229, 255), true)
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

    /** All library management surfaces share this scrollable, keyboard-resizing native dialog. */
    private fun management(title: String, build: (Dialog, LinearLayout, TextView) -> Unit) {
        managementDialog?.dismiss()
        val dialog = Dialog(this)
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE)
        val body = column().apply { setPadding(dp(24), dp(20), dp(24), dp(20)) }
        body.addView(text(title, 22f, ink, true))
        val error = text("", 14f, Color.rgb(166, 45, 45)).apply {
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
            setBackgroundDrawable(GradientDrawable().apply { setColor(Color.WHITE); cornerRadius = dp(24).toFloat() })
            decorView.clipToOutline = true
            addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            setDimAmount(0.28f)
            setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        }
        dialog.setOnDismissListener { if (managementDialog === dialog) managementDialog = null }
        managementDialog = dialog
        dialog.show()
        dialog.window?.setLayout(minOf(dp(480), dp(resources.configuration.screenWidthDp) - dp(32)), WindowManager.LayoutParams.WRAP_CONTENT)
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
            body.addView(text(if (parent == null) "최상위 폴더" else folderPath(snapshot, parent), 14f, muted))
            val edit = EditText(this).apply {
                hint = "폴더 이름"; contentDescription = "폴더 이름"; setSingleLine(true)
                setText(folder?.name.orEmpty()); setSelectAllOnFocus(true); minHeight = dp(52)
                setTextColor(ink); setHintTextColor(muted); textSize = 16f
                setPadding(dp(16), dp(12), dp(16), dp(12)); background = surface(Color.rgb(245, 245, 248))
            }
            body.addView(edit, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(12); bottomMargin = dp(12) })
            body.addView(button("저장", true) {
                if (libraryAction(error) {
                    if (id == null) vemo().library.createFolder(edit.text.toString().trim(), parent)
                    else vemo().library.renameFolder(id, edit.text.toString().trim())
                }) { dialog.dismiss(); render() }
            })
        }
    }
    private fun folderActions(id: String) {
        val folder = library()?.folders?.firstOrNull { it.id == id } ?: return
        management(folder.name) { dialog, body, _ ->
            fun action(title: String, color: Int = purple, run: () -> Unit) { body.addView(button(title) { dialog.dismiss(); run() }.apply { setTextColor(color) }) }
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
            for (target in destinations) {
                if (!canMove(item, target, snapshot)) continue
                val title = if (target == null) { if (item.folder) "최상위 폴더" else "미분류" } else folderPath(snapshot, target)
                body.addView(button(title) {
                    if (libraryAction(error) { moveItem(item, target) }) { dialog.dismiss(); render() }
                }.apply { gravity = Gravity.START or Gravity.CENTER_VERTICAL })
            }
            if (destinations.none { canMove(item, it, snapshot) }) body.addView(text("이동할 다른 폴더가 없습니다.", 15f, muted))
        }
    }
    private fun detail(id: String) {
        val item = try { vemo().store.get(id) } catch (_: Exception) { detailId = null; recordings(); return }
        val result = try { vemo().store.result(item) } catch (_: Exception) { null }

        label(result?.title ?: item.title)
        content.addView(text(date(item.recordedAt), 14f, muted))
        content.addView(text("${state(item)} · ${duration(item.durationMs)} · ${item.bytes / 1024} KB", 14f, purple))
        content.addView(text(item.status, 14f, muted))
        val folder = library()?.recordingFolders?.get(id)
        val path = if (folder == null) "미분류" else try { vemo().library.path(folder) } catch (_: Exception) { "폴더 확인 필요" }
        content.addView(text(path, 14f, muted))
        content.addView(button("폴더 이동") { moveRecording(id) })
        if (item.state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) {
            content.addView(button("진행 중인 녹음 열기", true) { tab = "record"; detailId = null; render() })
        }
        gap()
        if (item.uploadable) {
            content.addView(button(if (playerId == item.id) "재생 중지" else "녹음 재생", true) { play(item) }.apply { isEnabled = !vemo().gate.isRecording() })
            content.addView(row().apply {
                addView(button("폴더에 복사") { vemo().export(id) }, LinearLayout.LayoutParams(0, -2, 1f).apply { marginEnd = dp(8) })
                addView(button("공유") { try { startActivity(Intent.createChooser(ShareAudio.intent(this@MainActivity, item), "녹음 공유")) } catch (error: Exception) { vemo().notify(safeError(error)) } }, LinearLayout.LayoutParams(0, -2, 1f))
            })
            if (vemo().cloudConfigured()) content.addView(button("동기화 다시 시도 / PC 결과 확인") { vemo().sync(true) }.apply { isEnabled = !vemo().gate.busy() })
        } else if (item.state == CaptureState.INTERRUPTED) content.addView(button("보존한 오디오 복구 시도") { vemo().recover(id) })
        gap()
        content.addView(row().apply {
            for ((key, title) in listOf("transcript" to "전사", "notes" to "노트")) {
                addView(button(title) { detailTab = key; render() }.apply {
                    isSelected = detailTab == key
                    val fill = if (isSelected) Color.rgb(240, 239, 255) else Color.rgb(245, 245, 248)
                    background = android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x226258EE), surface(fill), null)
                    setTextColor(if (isSelected) Color.rgb(85, 75, 214) else muted)
                    contentDescription = title + if (isSelected) ", 선택됨" else ""
                }, LinearLayout.LayoutParams(0, -2, 1f).apply { if (key == "transcript") marginEnd = dp(8) })
            }
        })
        gap()
        if (result != null) {
            content.addView(text(if (detailTab == "notes") result.notes else result.transcript, 16f).apply {
                setTextIsSelectable(true); setLineSpacing(dp(6).toFloat(), 1.15f)
            })
        } else {
            content.addView(text(if (item.mode == Mode.DESKTOP && item.uploadable)
                if (item.uploaded) "PC 결과 대기 중. 전사와 노트가 완료되면 여기에 표시됩니다."
                else "클라우드 연결 후 완료한 녹음을 업로드하세요. 기기 원본은 오프라인에서도 유지됩니다."
                else "아직 생성된 전사와 노트가 없습니다.", 15f, muted))
        }
        gap()
        if (item.state !in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING))
            content.addView(button("목록에서 숨기기") { hideRecording(id) })
    }
    private fun settings() {
        label("설정")
        statusView = text(vemo().message, 13f, muted)
        content.addView(statusView)
        gap()
        label("기본 녹음 모드")
        val group = RadioGroup(this)
        val local = RadioButton(this).apply { id = View.generateViewId(); text = "녹음만 · 폴더 저장/공유"; minHeight = dp(52); setTextColor(ink) }
        val desktop = RadioButton(this).apply { id = View.generateViewId(); text = "PC 자동 처리 · 클라우드 연결 시"; minHeight = dp(52); setTextColor(ink) }
        group.addView(local); group.addView(desktop); group.check(if (vemo().mode() == Mode.DESKTOP) desktop.id else local.id)
        group.setOnCheckedChangeListener { _, selected -> vemo().preferences.edit().putString("mode", if (selected == desktop.id) "desktop" else "record_only").apply() }
        content.addView(group)
        gap(20)
        label("저장 및 연결")
        content.addView(text("자동 동기화: " + if (vemo().preferences.getString("provider", "nextcloud") == "google") "Google Drive" else "Nextcloud", 14f, muted))
        if (vemo().syncing && vemo().preferences.getString("provider", "nextcloud") == "google") content.addView(button("Drive 동기화 취소") { vemo().google.cancel(); vemo().notify("Drive 동기화 취소 요청 · 다시 시도할 수 있습니다.") })
        content.addView(text("기기 원본은 앱 내부에 보관합니다. 앱 삭제/데이터 초기화 전에는 폴더 복사 또는 공유로 내보내세요.", 14f, muted))
        gap()
        label("시스템 저장 폴더")
        content.addView(text(if (vemo().preferences.getString("folder", null) == null) "선택한 폴더 없음" else "시스템 폴더 선택됨 · 녹음 완료 후 자동 복사", 14f, muted))
        content.addView(button("저장 폴더 선택") {
            try { startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION), 51) }
            catch (error: Exception) { vemo().notify(safeError(error)) }
        })
        if (vemo().preferences.contains("folder")) content.addView(button("폴더 자동 복사 해제") {
            vemo().preferences.getString("folder", null)?.let { try { contentResolver.releasePersistableUriPermission(Uri.parse(it), Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) } catch (_: Exception) { } }
            vemo().preferences.edit().remove("folder").apply(); render()
        })
        content.addView(text("표시되는 폴더 공급자는 Android가 결정합니다. Google Drive가 폴더 선택에 표시되지 않으면 녹음의 공유 버튼에서 Drive를 선택하세요. 공유는 자동 PC 처리 연결과 별개입니다.", 13f, muted))
        gap(20)
        googleSettings()
        gap(20)
        label("Nextcloud · 선택 사항")
        content.addView(text("HTTPS WebDAV 폴더로 완료한 녹음과 PC 결과를 교환합니다. 데스크톱에서 같은 폴더를 등록하세요.", 14f, muted))
        val old = try { vemo().vault.load() } catch (_: Exception) { null }
        fun field(caption: String, initial: String = "", secret: Boolean = false): EditText {
            content.addView(text(caption, 13f, muted))
            val edit = EditText(this).apply {
                setText(initial); setSingleLine(true); textSize = 15f; minHeight = dp(48)
                isSaveEnabled = false; importantForAutofill = View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS
                inputType = if (secret) InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD else InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD
            }
            content.addView(edit)
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
        label("Google Drive · 선택 사항")
        val binding = try { vemo().google.binding() } catch (_: Exception) { null }
        content.addView(text(if (binding == null) "Mac에서 먼저 Google Drive 처리기를 등록한 뒤 연결하세요." else "Google 계정 ID: ${binding.accountId}\n연결된 Drive root: ${binding.rootId}\n처리기: ${binding.processorId}", 14f, muted))
        content.addView(text("Vemo 앱 파일 권한만 요청합니다. Google 프로젝트 vemo-511910의 Android OAuth 등록이 필요합니다.", 13f, muted))
        content.addView(button("Google 로그인 / 재승인 및 폴더 선택", true) {
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
                    vemo().notify("Google 로그인 실패 · Cloud 프로젝트의 Android 패키지/서명 등록과 네트워크를 확인하세요.")
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
                        if (roots.isEmpty()) vemo().notify("접근 가능한 Vemo root가 없습니다. 같은 Google 계정으로 Mac에서 먼저 등록하세요.")
                        else AlertDialog.Builder(this).setTitle("Mac 처리기 폴더 선택")
                            .setItems(roots.map { "${it.name} · ${it.processorId} · ${it.id}" }.toTypedArray()) { _, index ->
                                selectGoogle(DriveBinding(account, roots[index].id, roots[index].processorId))
                            }.setNegativeButton("취소", null).show()
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
    private fun play(item: Recording) {
        if (playerId == item.id) { stopPlayer(); render(); return }
        stopPlayer()
        try {
            player = MediaPlayer().apply {
                setDataSource(vemo().store.audio(item.id).absolutePath)
                setOnCompletionListener { stopPlayer(); render() }
                setOnErrorListener { _, _, _ -> stopPlayer(); vemo().notify("오디오를 재생할 수 없습니다. 원본은 보존했습니다."); true }
                prepare(); start()
            }
            playerId = item.id; render()
        } catch (error: Exception) { stopPlayer(); vemo().notify(safeError(error)) }
    }
    private fun stopPlayer() { try { player?.release() } catch (_: Exception) { }; player = null; playerId = null }
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
