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
import android.view.*
import android.widget.*
import com.vemo.android.core.*
import com.google.android.gms.auth.api.identity.Identity
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter

/** Small native Android UI. No mobile inference controls until that runtime exists. */
class MainActivity : Activity() {
    private val purple = Color.rgb(117, 66, 200)
    private val ink = Color.rgb(40, 31, 53)
    private val muted = Color.rgb(103, 94, 117)
    private val background = Color.rgb(248, 245, 252)
    private lateinit var container: LinearLayout
    private lateinit var content: LinearLayout
    private var tab = "record"
    private var detailId: String? = null
    private var timer: TextView? = null
    private var statusView: TextView? = null
    private var player: MediaPlayer? = null
    private var playerId: String? = null
    private val handler = Handler(Looper.getMainLooper())
    private val listener: () -> Unit = { if (tab == "settings") statusView?.text = vemo().message else render() }
    private val tick = object : Runnable {
        override fun run() {
            timer?.text = duration(vemo().elapsedMs)
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
        tab = savedInstanceState?.getString("tab") ?: "record"
        detailId = savedInstanceState?.getString("detail")
        window.statusBarColor = background
        window.navigationBarColor = background
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
        render()
    }
    override fun onSaveInstanceState(outState: Bundle) { super.onSaveInstanceState(outState); outState.putString("tab", tab); outState.putString("detail", detailId) }
    override fun onResume() { super.onResume(); vemo().listeners.add(listener); handler.post(tick); handler.postDelayed(poll, 30_000); SyncJobService.schedule(this); render() }
    override fun onPause() { vemo().listeners.remove(listener); handler.removeCallbacks(tick); handler.removeCallbacks(poll); stopPlayer(); super.onPause() }
    override fun onDestroy() { stopPlayer(); handler.removeCallbacksAndMessages(null); super.onDestroy() }
    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
    private fun text(value: String, size: Float = 16f, color: Int = ink, bold: Boolean = false): TextView = TextView(this).apply {
        text = value; textSize = size; setTextColor(color); setPadding(0, dp(5), 0, dp(5)); if (bold) setTypeface(null, Typeface.BOLD)
    }
    private fun column(): LinearLayout = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
    private fun row(): LinearLayout = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
    private fun button(label: String, primary: Boolean = false, action: () -> Unit): Button = Button(this).apply {
        text = label; isAllCaps = false; textSize = 15f; minHeight = dp(48)
        setTextColor(if (primary) Color.WHITE else purple)
        backgroundTintList = android.content.res.ColorStateList.valueOf(if (primary) purple else Color.rgb(238, 231, 249))
        setOnClickListener { action() }
    }
    private fun gap(height: Int = 12) { content.addView(Space(this), LinearLayout.LayoutParams(1, dp(height))) }
    private fun label(value: String) { content.addView(text(value, 22f, ink, true)) }
    private fun card(): LinearLayout = column().apply {
        setPadding(dp(18), dp(16), dp(18), dp(16))
        background = GradientDrawable().apply { setColor(Color.WHITE); cornerRadius = dp(20).toFloat() }
    }
    private fun render() {
        timer = null
        container = column().apply { setBackgroundColor(this@MainActivity.background) }
        container.setOnApplyWindowInsetsListener { view, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                view.setPadding(bars.left + dp(20), bars.top + dp(12), bars.right + dp(20), bars.bottom + dp(8))
            } else view.setPadding(dp(20), dp(28), dp(20), dp(10))
            insets
        }
        container.addView(row().apply {
            addView(text("Vemo", 30f, purple, true), LinearLayout.LayoutParams(0, dp(52), 1f))
            addView(text("당신의 목소리, 기록으로", 12f, muted))
        })
        statusView = text(vemo().message, 13f, muted)
        container.addView(statusView)
        val scroll = ScrollView(this).apply { isFillViewport = true }
        content = column().apply { setPadding(0, dp(12), 0, dp(12)) }
        scroll.addView(content)
        container.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        container.addView(row().apply {
            for ((key, title) in listOf("record" to "녹음", "list" to "목록", "settings" to "설정")) {
                addView(button(title, tab == key) { stopPlayer(); detailId = null; tab = key; render() }, LinearLayout.LayoutParams(0, dp(52), 1f))
            }
        })
        setContentView(container)
        container.requestApplyInsets()
        when (tab) { "settings" -> settings(); "list" -> if (detailId == null) recordings() else detail(detailId!!); else -> record() }
    }
    private fun record() {
        val active = vemo().store.list().firstOrNull { it.state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING) }
        label(if (active == null) "새로운 녹음" else if (active.state == CaptureState.PAUSED) "잠시 쉬는 중" else "지금 녹음 중")
        gap()
        content.addView(card().apply {
            addView(text("●", 64f, purple).apply { gravity = Gravity.CENTER })
            timer = text(if (active == null) "00:00" else duration(vemo().elapsedMs), 48f, ink, true).apply { gravity = Gravity.CENTER }
            addView(timer)
            addView(text(if (active == null) "완료 전에는 업로드하지 않습니다." else "${if (active.state == CaptureState.PAUSED) "일시정지" else "기기에 저장 중"} · 원본은 앱 내부에 보관", 14f, muted).apply { gravity = Gravity.CENTER })
        })
        gap(20)
        if (active == null) {
            val group = RadioGroup(this)
            val local = RadioButton(this).apply { id = View.generateViewId(); text = "녹음만 · 폴더 저장/공유" }
            val desktop = RadioButton(this).apply { id = View.generateViewId(); text = "PC 자동 처리 · 클라우드 연결 시" }
            group.addView(local); group.addView(desktop); group.check(if (vemo().mode() == Mode.DESKTOP) desktop.id else local.id)
            group.setOnCheckedChangeListener { _, selected -> vemo().preferences.edit().putString("mode", if (selected == desktop.id) "desktop" else "record_only").apply() }
            content.addView(group)
            gap()
            content.addView(button("녹음 시작", true) { startRecording() }.apply { isEnabled = !vemo().gate.busy() })
            content.addView(text("마이크 권한이 필요합니다. 녹음 중에는 알림에서 일시정지·완료할 수 있습니다.", 13f, muted))
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
        try { startForegroundService(Intent(this, RecordingService::class.java).setAction("com.vemo.START")) }
        catch (error: Exception) { vemo().notify(safeError(error)) }
    }
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 41 && grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED || requestCode == 42) startRecording()
        else vemo().notify("녹음하려면 설정에서 마이크 권한을 허용하세요.")
    }
    private fun command(action: String) { startService(Intent(this, RecordingService::class.java).setAction("com.vemo.$action")) }
    private fun recordings() {
        label("녹음 목록")
        content.addView(text("기기 원본과 서버 도착 상태를 따로 표시합니다.", 13f, muted))
        val items = vemo().store.list()
        if (items.isEmpty()) content.addView(text("아직 녹음이 없습니다. 첫 녹음을 시작하세요.", 17f, muted))
        for (item in items) {
            gap()
            val result = try { vemo().store.result(item) } catch (_: Exception) { null }
            content.addView(card().apply {
                addView(text(result?.title ?: item.title, 19f, ink, true))
                addView(text("${date(item.recordedAt)} · ${duration(item.durationMs)} · ${item.bytes / 1024} KB", 13f, muted))
                addView(text(state(item), 14f, purple))
                if (item.status.isNotBlank()) addView(text(item.status, 13f, muted))
                addView(button("열기") { detailId = item.id; render() })
            })
        }
        gap()
        if (vemo().cloudConfigured()) content.addView(button(if (vemo().syncing) "동기화 중…" else "완료한 녹음 업로드 / 결과 확인") { vemo().sync(true) }.apply { isEnabled = !vemo().gate.busy() })
    }
    private fun detail(id: String) {
        val item = try { vemo().store.get(id) } catch (_: Exception) { detailId = null; recordings(); return }
        val result = try { vemo().store.result(item) } catch (_: Exception) { null }
        content.addView(button("‹ 목록") { detailId = null; stopPlayer(); render() })
        label(result?.title ?: item.title)
        content.addView(text(date(item.recordedAt), 14f, muted))
        content.addView(text("${state(item)} · ${duration(item.durationMs)} · ${item.bytes / 1024} KB", 14f, purple))
        content.addView(text(item.status, 14f, muted))
        gap()
        if (item.uploadable) {
            content.addView(button(if (playerId == item.id) "재생 중지" else "녹음 재생", true) { play(item) }.apply { isEnabled = !vemo().gate.isRecording() })
            content.addView(row().apply {
                addView(button("폴더에 복사") { vemo().export(id) }, LinearLayout.LayoutParams(0, dp(52), 1f))
                addView(button("공유") { try { startActivity(Intent.createChooser(ShareAudio.intent(this@MainActivity, item), "녹음 공유")) } catch (error: Exception) { vemo().notify(safeError(error)) } }, LinearLayout.LayoutParams(0, dp(52), 1f))
            })
            if (vemo().cloudConfigured()) content.addView(button("동기화 다시 시도 / PC 결과 확인") { vemo().sync(true) }.apply { isEnabled = !vemo().gate.busy() })
        } else if (item.state == CaptureState.INTERRUPTED) content.addView(button("보존한 오디오 복구 시도") { vemo().recover(id) })
        gap()
        if (result != null) {
            label("전사")
            content.addView(text(result.transcript).apply { setTextIsSelectable(true) })
            gap()
            label("상세 노트")
            content.addView(text(result.notes).apply { setTextIsSelectable(true) })
        } else if (item.mode == Mode.DESKTOP && item.uploadable) {
            label("PC 결과 대기 중")
            content.addView(text(if (item.uploaded) "서버에 저장했습니다. 등록한 PC에서 전사와 노트가 완료되면 여기에 표시됩니다." else "클라우드 연결 후 완료한 녹음을 업로드하세요. 기기 원본은 오프라인에서도 유지됩니다.", 15f, muted))
        }
        gap()
        if (item.state !in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) content.addView(button("목록에서 숨기기") {
            AlertDialog.Builder(this).setTitle("목록에서 숨길까요?").setMessage("기기 원본은 보존합니다. 클라우드 파일은 삭제하지 않으며 이 항목을 자동으로 다시 가져오지 않습니다.")
                .setNegativeButton("취소", null).setPositiveButton("숨기기") { _, _ -> vemo().store.hide(id); detailId = null; render() }.show()
        })
    }
    private fun settings() {
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
