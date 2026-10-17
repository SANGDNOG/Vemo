package com.vemo.android

import android.content.*
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import com.vemo.android.core.*
import java.io.FileNotFoundException

class AudioShareProvider : ContentProvider() {
    override fun onCreate() = true
    private fun record(uri: Uri): Recording {
        try {
            if (uri.scheme != "content" || uri.authority != "${context!!.packageName}.audio" || uri.pathSegments.size != 1 || uri.query != null || uri.fragment != null) throw ProtocolException("Invalid share URI")
            val id = validUuid(uri.pathSegments.single())
            val record = context!!.vemo().store.get(id)
            if (!record.uploadable) throw ProtocolException("Only complete recordings can be shared")
            return record
        } catch (_: Exception) { throw FileNotFoundException("Completed recording unavailable") }
    }
    override fun getType(uri: Uri): String { record(uri); return "audio/mp4" }
    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor {
        val record = record(uri)
        val columns = projection ?: arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)
        return MatrixCursor(columns).apply { addRow(columns.map { when (it) {
            OpenableColumns.DISPLAY_NAME -> "${record.id}.m4a"
            OpenableColumns.SIZE -> record.bytes
            else -> null
        } }) }
    }
    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        if (mode != "r") throw FileNotFoundException("Read-only audio share")
        val item = record(uri)
        val file = context!!.vemo().store.audio(item.id)
        if (file.length() != item.bytes) throw FileNotFoundException("Audio size changed")
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
    }
    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException("Read only")
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException("Read only")
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException("Read only")
}
object ShareAudio {
    fun intent(context: Context, record: Recording): Intent {
        if (!record.uploadable) throw ProtocolException("완료한 녹음만 공유할 수 있습니다.")
        val uri = Uri.parse("content://${context.packageName}.audio/${record.id}")
        return Intent(Intent.ACTION_SEND).setType("audio/mp4").putExtra(Intent.EXTRA_STREAM, uri)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION).apply { clipData = ClipData.newRawUri("Vemo audio", uri) }
    }
}
