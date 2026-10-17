package com.vemo.android

import android.app.Application
import android.content.Context
import android.os.Handler
import android.os.Looper
import com.vemo.android.core.*
import java.io.File
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.Executors

class VemoApp : Application() {
    lateinit var store: RecordingStore; private set
    val gate = OperationGate()
    val io = Executors.newSingleThreadExecutor()
    val listeners = CopyOnWriteArraySet<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())
    lateinit var vault: CredentialVault; private set
    val preferences by lazy { getSharedPreferences("preferences", Context.MODE_PRIVATE) }
    @Volatile var message = "녹음은 기기에 먼저 안전하게 저장됩니다."
    @Volatile var elapsedMs = 0L
    @Volatile var syncing = false
    override fun onCreate() {
        super.onCreate()
        store = RecordingStore(File(filesDir, "recordings"))
        store.recoverInterrupted()
        vault = CredentialVault(this)
    }
    fun changed() { main.post { listeners.forEach { it() } } }
    fun notify(message: String) { this.message = message; changed() }
    fun mode(): Mode = if (preferences.getString("mode", "record_only") == "desktop") Mode.DESKTOP else Mode.RECORD_ONLY
    fun sync(manual: Boolean = false, done: (() -> Unit)? = null) {
        if (!gate.beginSync()) { if (manual) notify("녹음/일시정지 중에는 동기화하지 않습니다. 진행 중 작업이 끝나면 다시 시도하세요."); done?.invoke(); return }
        syncing = true; changed()
        io.execute {
            try {
                val credentials = vault.load() ?: throw ProtocolException("Nextcloud는 선택 기능입니다. 설정에서 연결하거나 폴더 저장/공유를 이용하세요.")
                val cloud = WebDavCloud(DavConfig(credentials.endpoint, credentials.folder), credentials.user, credentials.password)
                val engine = SyncEngine(cloud)
                var processor: Processor? = null
                var failures = 0
                var checked = 0
                for (original in store.list().filter { it.uploadable && (manual || it.mode == Mode.DESKTOP) }) {
                    if (Thread.currentThread().isInterrupted) break
                    try {
                        if (original.cloudKey != null && original.cloudKey != credentials.identity()) throw ProtocolException("이 녹음은 다른 클라우드 폴더에 연결되어 있습니다. 원래 연결로 돌아가세요.")
                        if (original.mode == Mode.DESKTOP && processor == null) processor = engine.processor()
                        if (original.targetProcessor != null && original.targetProcessor != processor?.id) throw ProtocolException("등록된 데스크톱 처리기가 다릅니다. 원본과 이전 연결을 보존했습니다.")
                        val record = store.update(original.id) {
                            it.copy(targetProcessor = it.targetProcessor ?: if (it.mode == Mode.DESKTOP) processor?.id else null, cloudKey = it.cloudKey ?: credentials.identity(), status = "동기화 중…")
                        }
                        if (!record.uploaded) {
                            engine.upload(record, store.audio(record.id))
                            store.update(record.id) { it.copy(uploaded = true, status = if (it.mode == Mode.DESKTOP) "서버 저장 완료 · PC 결과 대기 중" else "서버 저장 완료 · 녹음만") }
                        }
                        if (record.mode == Mode.DESKTOP) {
                            val result = engine.result(record)
                            if (result != null) {
                                store.saveResult(record, result.second)
                                store.update(record.id) { it.copy(status = "PC 전사·노트 수신 완료") }
                            } else store.update(record.id) { it.copy(status = "서버 저장 완료 · PC 결과 대기 중") }
                        }
                        checked++
                    } catch (error: Exception) {
                        failures++
                        store.update(original.id) { it.copy(status = safeError(error)) }
                    }
                    changed()
                }
                message = "동기화 확인: ${checked}개 완료" + if (failures > 0) " · ${failures}개 오류 (각 항목에서 확인)" else ""
            } catch (error: Exception) { message = safeError(error) }
            finally {
                syncing = false; gate.endSync(); changed(); done?.invoke()
            }
        }
    }
    fun export(id: String, done: (() -> Unit)? = null) {
        if (!gate.beginSync()) { notify("녹음 또는 동기화가 끝나면 폴더에 복사하세요."); return }
        io.execute {
            try {
                val record = store.get(id)
                if (!record.uploadable) throw ProtocolException("완료한 녹음만 폴더로 복사할 수 있습니다.")
                val uri = preferences.getString("folder", null) ?: throw ProtocolException("설정에서 저장 폴더를 먼저 선택하세요.")
                FolderExport.copy(this, android.net.Uri.parse(uri), store.audio(id), record)
                store.update(id) { it.copy(exported = true, status = "선택한 폴더에 복사 완료 · 기기 원본 보존") }
                message = "폴더에 복사했습니다."
            } catch (error: Exception) {
                message = safeError(error)
                store.update(id) { it.copy(status = message) }
            } finally { gate.endSync(); changed(); done?.invoke() }
        }
    }
    fun recover(id: String) {
        if (!gate.beginRecording()) { notify("진행 중 작업을 마친 뒤 복구하세요."); return }
        io.execute {
            try {
                val candidate = if (store.audio(id).exists()) store.audio(id) else store.partial(id)
                val duration = AudioValidation.duration(candidate)
                store.transition(id, CaptureState.FINALIZING)
                store.finish(id, duration)
                message = "중단된 오디오를 복구했습니다."
            } catch (error: Exception) {
                if (store.get(id).state == CaptureState.FINALIZING) store.transition(id, CaptureState.INTERRUPTED)
                store.update(id) { it.copy(status = "복구할 수 있는 완성된 오디오가 없습니다. 중단된 원본은 기기에 보존했습니다.") }
                message = "오디오 복구에 실패했습니다. 원본은 보존했습니다."
            } finally { gate.endRecording(); changed() }
        }
    }
}
fun Context.vemo(): VemoApp = applicationContext as VemoApp
fun safeError(error: Exception): String = when (error) {
    is AuthException -> "서버 인증/권한 오류 · 설정에서 자격 증명을 확인하세요."
    is ConflictException -> "원격 콘텐츠 충돌 · 덮어쓰지 않았습니다. 기기 원본을 보존했습니다."
    is ProtocolException -> error.message ?: "동기화 계약 검증 오류"
    is SecurityException -> "권한이 취소되었습니다. 마이크 또는 저장 폴더 권한을 다시 허용하세요. 원본은 보존했습니다."
    is java.security.GeneralSecurityException -> "보호된 로그인 정보를 열 수 없습니다. 설정에서 다시 연결하세요."
    is javax.net.ssl.SSLException -> "HTTPS 인증서/연결 검증에 실패했습니다."
    is java.net.UnknownHostException, is java.net.ConnectException, is java.net.SocketTimeoutException -> "네트워크 연결이 없습니다/시간이 초과되었습니다. 기기 원본은 보존했습니다. 다시 시도하세요."
    is java.io.IOException -> "저장 공간, 폴더 권한 또는 네트워크 오류 · 원본을 보존했습니다. 공간/연결 확인 후 다시 시도하세요."
    else -> "작업을 완료하지 못했습니다. 원본은 보존했습니다."
}
