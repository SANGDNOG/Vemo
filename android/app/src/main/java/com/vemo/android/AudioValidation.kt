package com.vemo.android

import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMetadataRetriever
import com.vemo.android.core.ProtocolException
import java.io.File

object AudioValidation {
    fun duration(file: File): Long {
        val extractor = MediaExtractor()
        try {
            extractor.setDataSource(file.absolutePath)
            var valid = false
            for (index in 0 until extractor.trackCount) {
                val format = extractor.getTrackFormat(index)
                if (format.getString(MediaFormat.KEY_MIME)?.startsWith("audio/") == true) {
                    extractor.selectTrack(index)
                    val buffer = java.nio.ByteBuffer.allocate(64 * 1024)
                    if (extractor.readSampleData(buffer, 0) > 0) valid = true
                    break
                }
            }
            if (!valid) throw ProtocolException("오디오 컨테이너를 복구할 수 없습니다. 원본은 보존했습니다.")
        } finally { extractor.release() }
        val retriever = MediaMetadataRetriever()
        try {
            retriever.setDataSource(file.absolutePath)
            val duration = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0
            if (duration <= 0) throw ProtocolException("완료한 오디오가 아닙니다. 원본은 보존했습니다.")
            return duration
        } finally { retriever.release() }
    }
}
