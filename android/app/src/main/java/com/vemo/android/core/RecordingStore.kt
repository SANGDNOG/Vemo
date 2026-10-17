package com.vemo.android.core

import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.LinkOption.NOFOLLOW_LINKS
import java.nio.file.StandardCopyOption
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter
import java.util.UUID

/** One private directory per immutable recording. Atomic metadata never replaces the original audio. */
class RecordingStore(private val dir: File) {
    init { if (!dir.exists() && !dir.mkdirs()) throw java.io.IOException("Cannot create private recording storage") }
    private fun folder(id: String): File {
        validUuid(id)
        val folder = File(dir, id)
        if (Files.isSymbolicLink(dir.toPath()) || Files.isSymbolicLink(folder.toPath())) throw ProtocolException("Symlink recording directory rejected")
        return folder
    }
    @Synchronized fun create(title: String, mode: Mode, processor: String?): Recording {
        processor?.let { validUuid(it) }
        val id = UUID.randomUUID().toString()
        val folder = folder(id)
        if (!folder.mkdir()) throw java.io.IOException("Cannot create recording")
        val record = Recording(id, title.trim().take(512).ifEmpty { "녹음" }, OffsetDateTime.now().format(DateTimeFormatter.ISO_OFFSET_DATE_TIME), mode, processor, CaptureState.RECORDING)
        save(record)
        return record
    }
    fun partial(id: String): File = File(folder(id), "capture.m4a").also { rejectLink(it) }
    fun audio(id: String): File = File(folder(id), "audio.m4a").also { rejectLink(it) }
    private fun rejectLink(file: File) { if (Files.isSymbolicLink(file.toPath())) throw ProtocolException("Symlink audio rejected") }
    @Synchronized fun get(id: String): Recording {
        val file = File(folder(id), "local.json")
        rejectLink(file)
        val obj = file.inputStream().use { Json.parse(readBounded(it, Wire.MAX_JSON)) }
        val item = try { Json.gson.fromJson(obj, Recording::class.java) } catch (_: Exception) { throw ProtocolException("Damaged local recording metadata; audio preserved") }
        if (item.id != id || item.title == null || item.mode == null || item.state == null) throw ProtocolException("Damaged recording identity")
        validDate(item.recordedAt)
        item.hash?.let { validHash(it) }
        item.targetProcessor?.let { validUuid(it) }
        if (item.state == CaptureState.COMPLETE && (item.hash == null || item.bytes <= 0)) throw ProtocolException("Incomplete local metadata")
        return item
    }
    @Synchronized fun list(): List<Recording> = dir.listFiles().orEmpty().filter { it.isDirectory && !Files.isSymbolicLink(it.toPath()) }
        .mapNotNull { try { get(it.name) } catch (_: Exception) { null } }.filterNot { it.hidden }.sortedByDescending { it.recordedAt }
    @Synchronized fun unreadableCount(): Int = dir.listFiles().orEmpty().count { it.isDirectory && try { get(it.name); false } catch (_: Exception) { true } }
    private fun save(record: Recording) { atomicWrite(File(folder(record.id), "local.json"), Json.bytes(record)) }
    @Synchronized fun update(id: String, transform: (Recording) -> Recording): Recording {
        val old = get(id)
        val next = transform(old)
        if (next.id != old.id || next.title != old.title || next.recordedAt != old.recordedAt || next.mode != old.mode || next.state != old.state || next.hash != old.hash || next.bytes != old.bytes || next.durationMs != old.durationMs || (old.targetProcessor != null && old.targetProcessor != next.targetProcessor) || (old.cloudKey != null && old.cloudKey != next.cloudKey)) throw ProtocolException("Immutable recording metadata changed")
        save(next)
        return next
    }
    @Synchronized fun transition(id: String, state: CaptureState): Recording {
        val old = get(id)
        val allowed = when (old.state) {
            CaptureState.RECORDING -> setOf(CaptureState.PAUSED, CaptureState.FINALIZING, CaptureState.INTERRUPTED, CaptureState.FAILED)
            CaptureState.PAUSED -> setOf(CaptureState.RECORDING, CaptureState.FINALIZING, CaptureState.INTERRUPTED, CaptureState.FAILED)
            CaptureState.INTERRUPTED -> setOf(CaptureState.FINALIZING, CaptureState.FAILED)
            CaptureState.FINALIZING -> setOf(CaptureState.INTERRUPTED, CaptureState.FAILED)
            CaptureState.COMPLETE, CaptureState.FAILED -> emptySet()
        }
        if (state !in allowed) throw ProtocolException("Invalid recording state transition")
        return old.copy(state = state).also { save(it) }
    }
    @Synchronized fun finish(id: String, duration: Long): Recording {
        val old = get(id)
        if (old.state != CaptureState.FINALIZING || duration <= 0) throw ProtocolException("Recording has not finalized")
        val destination = audio(id)
        val partial = partial(id)
        val source = if (destination.exists()) destination else partial
        if (!Files.isRegularFile(source.toPath(), NOFOLLOW_LINKS) || source.length() <= 0) throw ProtocolException("No recoverable audio; original retained")
        FileOutputStream(source, true).use { it.fd.sync() }
        val hash = sha256(source)
        val size = source.length()
        if (source != destination) {
            Files.move(source.toPath(), destination.toPath(), StandardCopyOption.ATOMIC_MOVE)
            syncDirectory(folder(id))
        }
        return old.copy(state = CaptureState.COMPLETE, hash = hash, bytes = size, durationMs = duration,
            status = "기기에 저장됨").also { save(it) }
    }
    @Synchronized fun recoverInterrupted() {
        for (item in list()) {
            if (item.state in setOf(CaptureState.RECORDING, CaptureState.PAUSED, CaptureState.FINALIZING)) {
                save(item.copy(state = CaptureState.INTERRUPTED, status = "녹음이 중단되었습니다. 원본을 보존했습니다. 복구를 시도하세요."))
            }
        }
    }
    @Synchronized fun hide(id: String) { update(id) { it.copy(hidden = true) } }
    @Synchronized fun saveResult(record: Recording, bytes: ByteArray) {
        Wire.result(bytes, Binding(record.id, record.hash!!, record.targetProcessor!!))
        atomicWrite(File(folder(record.id), "result.json"), bytes)
    }
    @Synchronized fun result(record: Recording): Result? {
        if (!record.uploadable || record.targetProcessor == null) return null
        val file = File(folder(record.id), "result.json")
        rejectLink(file)
        if (!file.exists()) return null
        return file.inputStream().use { Wire.result(readBounded(it, Wire.MAX_JSON), Binding(record.id, record.hash!!, record.targetProcessor)) }
    }
}
fun atomicWrite(file: File, bytes: ByteArray) {
    if (Files.isSymbolicLink(file.toPath())) throw ProtocolException("Symlink metadata rejected")
    val temporary = File.createTempFile(".metadata-", ".tmp", file.parentFile)
    try {
        FileOutputStream(temporary).use { it.write(bytes); it.fd.sync() }
        Files.move(temporary.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
        syncDirectory(file.parentFile!!)
    } finally { temporary.delete() }
}
private fun syncDirectory(folder: File) {
    java.nio.channels.FileChannel.open(folder.toPath(), java.nio.file.StandardOpenOption.READ).use { it.force(true) }
}
