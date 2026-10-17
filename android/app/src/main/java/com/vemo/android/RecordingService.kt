package com.vemo.android

import android.Manifest
import android.app.*
import android.content.Intent
import android.content.pm.PackageManager
import android.media.MediaRecorder
import android.os.*
import com.vemo.android.core.*
import java.io.FileOutputStream

class RecordingService : Service() {
    private var recorder: MediaRecorder? = null
    private var output: FileOutputStream? = null
    private var id: String? = null
    private var classificationWarning: String? = null
    private var paused = false
    private var finishing = false
    private var accumulated = 0L
    private var started = 0L
    private val handler = Handler(Looper.getMainLooper())
    private val ticker = object : Runnable {
        override fun run() {
            if (id == null || finishing) return
            vemo().elapsedMs = accumulated + if (paused) 0 else SystemClock.elapsedRealtime() - started
            notificationManager().notify(7, notification())
            handler.postDelayed(this, 1000)
        }
    }
    override fun onBind(intent: Intent?) = null
    override fun onCreate() {
        super.onCreate()
        notificationManager().createNotificationChannel(NotificationChannel("recording", "녹음", NotificationManager.IMPORTANCE_LOW))
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            "com.vemo.START" -> start(intent.getStringExtra("com.vemo.FOLDER_ID"))
            "com.vemo.PAUSE" -> pause()
            "com.vemo.RESUME" -> resume()
            "com.vemo.STOP" -> finish()
        }
        return START_NOT_STICKY
    }
    @Suppress("DEPRECATION")
    private fun start(folderId: String?) {
        val app = vemo()
        if (id != null || finishing) return
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) { app.notify("마이크 권한을 허용하세요."); stopSelf(); return }
        if (!app.gate.beginRecording()) { app.notify("진행 중 동기화가 끝나면 녹음을 시작하세요."); stopSelf(); return }
        try {
            if (filesDir.usableSpace < 16 * 1024 * 1024) throw ProtocolException("저장 공간이 부족하여 녹음을 시작하지 못했습니다.")
            startForeground(7, notification())
            val record = app.store.create("녹음", app.mode(), null)
            id = record.id
            val file = app.store.partial(record.id)
            // New private file only; no previous capture can be truncated.
            if (!file.createNewFile()) throw ProtocolException("Private capture already exists")
            output = FileOutputStream(file)
            val media = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(this) else MediaRecorder()
            recorder = media
            media.setAudioSource(MediaRecorder.AudioSource.MIC)
            media.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            media.setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
            media.setAudioSamplingRate(48000)
            media.setAudioEncodingBitRate(128000)
            media.setAudioChannels(1)
            media.setOutputFile(output!!.fd)
            media.setMaxFileSize(minOf(filesDir.usableSpace - 8 * 1024 * 1024, 2L * 1024 * 1024 * 1024))
            media.setOnErrorListener { _, _, _ -> interrupt("녹음 장치/저장 오류로 중단되었습니다. 원본은 보존했습니다.") }
            media.setOnInfoListener { _, what, _ -> if (what == MediaRecorder.MEDIA_RECORDER_INFO_MAX_FILESIZE_REACHED) finish() }
            media.prepare(); media.start()
            started = SystemClock.elapsedRealtime(); accumulated = 0; paused = false
            app.elapsedMs = 0
            handler.post(ticker)
            classificationWarning = null
            if (folderId != null) {
                try { app.library.assign(record.id, folderId) }
                catch (error: Exception) {
                    classificationWarning = "폴더 지정 실패 · 녹음은 계속되며 원본은 기기에 보존됩니다. ${safeError(error)}"
                }
            }
            app.notify(classificationWarning ?: "녹음 중 · 기기 내부에 저장합니다.")
        } catch (error: Exception) { interrupt(safeError(error)) }
    }
    private fun pause() {
        if (id == null || paused || finishing) return
        try {
            recorder!!.pause()
            accumulated += SystemClock.elapsedRealtime() - started
            paused = true
            vemo().store.transition(id!!, CaptureState.PAUSED)
            vemo().notify("일시정지 · 완료하거나 재개하세요. 업로드하지 않습니다.")
        } catch (error: Exception) { interrupt(safeError(error)) }
    }
    private fun resume() {
        if (id == null || !paused || finishing) return
        try {
            recorder!!.resume(); started = SystemClock.elapsedRealtime(); paused = false
            vemo().store.transition(id!!, CaptureState.RECORDING)
            vemo().notify("녹음 중")
        } catch (error: Exception) { interrupt(safeError(error)) }
    }
    private fun finish() {
        val current = id ?: return
        if (finishing) return
        finishing = true
        handler.removeCallbacks(ticker)
        val app = vemo()
        try {
            app.store.transition(current, CaptureState.FINALIZING)
            recorder!!.stop()
            release()
            app.notify("원본 오디오를 확정하고 있습니다…")
            app.io.execute {
                try {
                    val duration = AudioValidation.duration(app.store.partial(current))
                    val complete = app.store.finish(current, duration)
                    app.message = "기기에 저장 완료 · ${complete.bytes / 1024} KB" + (classificationWarning?.let { " · $it" } ?: "")
                } catch (error: Exception) {
                    if (app.store.get(current).state == CaptureState.FINALIZING) app.store.transition(current, CaptureState.INTERRUPTED)
                    app.store.update(current) { it.copy(status = safeError(error)) }
                    app.message = safeError(error)
                } finally {
                    app.gate.endRecording()
                    handler.post {
                        id = null; finishing = false
                        stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); app.changed()
                        if (app.store.get(current).uploadable) {
                            if (app.preferences.getString("folder", null) != null) app.export(current)
                            SyncJobService.schedule(app)
                        }
                    }
                }
            }
        } catch (error: Exception) { finishing = false; interrupt("완료 처리 실패 · ${safeError(error)}") }
    }
    private fun interrupt(message: String) {
        handler.removeCallbacks(ticker)
        try { recorder?.stop() } catch (_: Exception) { }
        release()
        id?.let { current ->
            try {
                val state = vemo().store.get(current).state
                if (state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) vemo().store.transition(current, CaptureState.INTERRUPTED)
                vemo().store.update(current) { it.copy(status = message) }
            } catch (_: Exception) { }
        }
        id = null; finishing = false
        vemo().gate.endRecording(); vemo().notify(message)
        stopForeground(STOP_FOREGROUND_REMOVE); stopSelf()
    }
    private fun release() {
        try { recorder?.release() } catch (_: Exception) { }
        recorder = null
        try { output?.fd?.sync() } catch (_: Exception) { }
        try { output?.close() } catch (_: Exception) { }
        output = null
    }
    override fun onDestroy() {
        if (id != null && !finishing) interrupt("녹음 서비스가 중단되었습니다. 원본을 보존했습니다.")
        handler.removeCallbacks(ticker)
        super.onDestroy()
    }
    private fun notificationManager() = getSystemService(NotificationManager::class.java)
    private fun notification(): Notification {
        val open = PendingIntent.getActivity(this, 1, Intent(this, MainActivity::class.java), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        fun action(name: String, code: Int) = PendingIntent.getService(this, code, Intent(this, RecordingService::class.java).setAction("com.vemo.$name"), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, "recording").setSmallIcon(com.vemo.android.R.drawable.ic_mic)
            .setContentTitle(if (paused) "Vemo · 일시정지" else "Vemo · 녹음 중")
            .setContentText("${vemo().elapsedMs / 1000}초 · 원본을 기기에 저장합니다")
            .setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true)
            .addAction(Notification.Action.Builder(null, if (paused) "재개" else "일시정지", action(if (paused) "RESUME" else "PAUSE", 2)).build())
            .addAction(Notification.Action.Builder(null, "완료", action("STOP", 3)).build()).build()
    }
}
