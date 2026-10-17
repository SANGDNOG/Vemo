package com.vemo.android

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import com.vemo.android.core.*
import java.io.File
import java.io.FileOutputStream

object FolderExport {
    /** SAF is an independent copy. Success never removes or moves the private original. */
    fun copy(context: Context, tree: Uri, source: File, record: Recording): Uri {
        if (tree.scheme != "content" || !DocumentsContract.isTreeUri(tree)) throw ProtocolException("유효한 시스템 저장 폴더를 선택하세요.")
        if (source.length() != record.bytes || sha256(source) != record.hash) throw ProtocolException("기기 원본의 해시가 일치하지 않습니다.")
        val resolver = context.contentResolver
        val parent = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        val name = "${record.id}-${record.hash}.m4a"
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree))
        val matches = mutableListOf<Uri>()
        resolver.query(children, arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { cursor ->
            var count = 0
            while (cursor.moveToNext()) {
                if (++count > 100_000) throw ProtocolException("선택한 폴더에 항목이 너무 많습니다.")
                if (cursor.getString(1) == name) matches += DocumentsContract.buildDocumentUriUsingTree(tree, cursor.getString(0))
            }
        }
        if (matches.size > 1) throw ConflictException()
        if (matches.size == 1) {
            val hash = resolver.openInputStream(matches[0])?.use { digest(it, record.bytes) }
            if (hash != record.hash) throw ConflictException()
            return matches[0]
        }
        val temporary = DocumentsContract.createDocument(resolver, parent, "audio/mp4", ".vemo-${record.id}-${java.util.UUID.randomUUID()}.partial")
            ?: throw java.io.IOException("Cannot create SAF document")
        var published = false
        try {
            val descriptor = resolver.openFileDescriptor(temporary, "w") ?: throw java.io.IOException("Cannot open SAF destination")
            descriptor.use {
                FileOutputStream(it.fileDescriptor).use { output ->
                    source.inputStream().use { input -> input.copyTo(output, 64 * 1024) }
                    output.flush()
                    try { output.fd.sync() } catch (_: java.io.SyncFailedException) { /* Some system providers use a pipe. Readback below verifies the copy. */ }
                }
            }
            val hash = resolver.openInputStream(temporary)?.use { digest(it, record.bytes) }
            if (hash != record.hash) throw ProtocolException("폴더 복사 검증 실패 · 기기 원본은 보존했습니다.")
            val destination = DocumentsContract.renameDocument(resolver, temporary, name)
                ?: throw ProtocolException("선택한 공급자는 완료 파일 이름 변경을 지원하지 않습니다. 다른 폴더 또는 공유를 이용하세요.")
            published = true
            return destination
        } finally {
            if (!published) try { DocumentsContract.deleteDocument(resolver, temporary) } catch (_: Exception) { }
        }
    }
}
