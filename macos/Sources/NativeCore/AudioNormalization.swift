import Foundation
import AVFoundation
import Darwin

/// Decodes only a local owned snapshot; bounded sample buffers are written directly to PCM.
public enum AudioNormalization {
    public static func normalize(input: URL, output: URL) async throws {
        guard input.isFileURL, output.isFileURL, input.standardizedFileURL != output.standardizedFileURL else {
            throw NativeFailure.invalid("오디오 입력과 출력 경로를 확인하세요.")
        }
        // Exclusive creation rejects every existing path, including aliases of the input.
        // Install cleanup only after this invocation owns a newly created output.
        let descriptor = open(output.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else { throw NativeFailure.invalid("새 PCM 출력을 만들지 못했습니다. 기존 출력 경로는 사용할 수 없습니다.") }
        let file = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        var completed = false
        defer {
            try? file.close()
            if !completed { try? FileManager.default.removeItem(at: output) }
        }
        try validateWaveContainer(input)
        let asset = AVURLAsset(url: input)
        let tracks = try await asset.loadTracks(withMediaType: .audio)
        guard let track = tracks.first else { throw NativeFailure.invalid("오디오 트랙이 없습니다.") }
        let duration = try await track.load(.timeRange).duration.seconds
        guard duration.isFinite, duration > 0, duration <= Double(UInt32.max - 36) / 32000 else { throw NativeFailure.invalid("오디오 길이를 확인하지 못했습니다.") }
        let reader = try AVAssetReader(asset: asset)
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: 16000,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false
        ]
        let samples = AVAssetReaderAudioMixOutput(audioTracks: [track], audioSettings: settings)
        samples.alwaysCopiesSampleData = false
        guard reader.canAdd(samples) else { throw NativeFailure.invalid("오디오 형식을 변환하지 못했습니다.") }
        reader.add(samples)
        guard reader.startReading() else { throw reader.error ?? NativeFailure.invalid("오디오를 읽지 못했습니다.") }
        defer { if reader.status == .reading { reader.cancelReading() } }
        try file.write(contentsOf: header(bytes: 0))
        var bytes: UInt64 = 0
        while let sample = samples.copyNextSampleBuffer() {
            guard CMSampleBufferDataIsReady(sample), let block = CMSampleBufferGetDataBuffer(sample),
                  let description = CMSampleBufferGetFormatDescription(sample),
                  let format = CMAudioFormatDescriptionGetStreamBasicDescription(description) else {
                throw NativeFailure.invalid("손상된 오디오 샘플입니다.")
            }
            guard format.pointee.mFormatID == kAudioFormatLinearPCM,
                  format.pointee.mSampleRate == 16000, format.pointee.mChannelsPerFrame == 1,
                  format.pointee.mBitsPerChannel == 16,
                  format.pointee.mFormatFlags & kAudioFormatFlagIsSignedInteger != 0,
                  format.pointee.mFormatFlags & (kAudioFormatFlagIsBigEndian | kAudioFormatFlagIsFloat) == 0 else {
                throw NativeFailure.invalid("PCM 변환 형식이 올바르지 않습니다.")
            }
            let count = CMBlockBufferGetDataLength(block)
            guard count == CMSampleBufferGetNumSamples(sample) * 2, count > 0,
                  bytes + UInt64(count) <= UInt64(UInt32.max) - 36 else {
                throw NativeFailure.invalid("PCM 크기가 올바르지 않거나 WAV 한도를 초과했습니다.")
            }
            var data = Data(count: count)
            let status = data.withUnsafeMutableBytes { CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: count, destination: $0.baseAddress!) }
            guard status == kCMBlockBufferNoErr else { throw NativeFailure.invalid("오디오 샘플을 읽지 못했습니다.") }
            try file.write(contentsOf: data)
            bytes += UInt64(count)
        }
        guard reader.status == .completed else { throw reader.error ?? NativeFailure.invalid("오디오 디코딩이 완료되지 않았습니다.") }
        // Allow codec priming/rounding, but never accept a silently shortened stream.
        guard bytes > 0, abs(Double(bytes / 2) / 16000 - duration) <= 0.1 else {
            throw NativeFailure.invalid("오디오가 비어 있거나 잘렸습니다.")
        }
        // AVFoundation's resampler can omit a few tail frames for filter latency.
        // Preserve the timeline duration after a completed, length-checked decode.
        let expectedBytes = UInt64((duration * 16000).rounded()) * 2
        guard expectedBytes <= UInt64(UInt32.max) - 36 else { throw NativeFailure.invalid("WAV 크기 한도를 초과했습니다.") }
        if bytes < expectedBytes {
            var remaining = expectedBytes - bytes
            while remaining > 0 {
                let count = Int(min(remaining, 65536))
                try file.write(contentsOf: Data(repeating: 0, count: count))
                remaining -= UInt64(count)
            }
        } else if bytes > expectedBytes {
            try file.truncate(atOffset: 44 + expectedBytes)
        }
        bytes = expectedBytes
        try file.seek(toOffset: 0)
        try file.write(contentsOf: header(bytes: UInt32(bytes)))
        try file.synchronize()
        completed = true
    }

    private static func header(bytes: UInt32) -> Data {
        var data = Data()
        func text(_ value: String) { data.append(contentsOf: value.utf8) }
        func number<T: FixedWidthInteger>(_ value: T) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }
        text("RIFF"); number(bytes + 36); text("WAVEfmt "); number(UInt32(16))
        number(UInt16(1)); number(UInt16(1)); number(UInt32(16000)); number(UInt32(32000))
        number(UInt16(2)); number(UInt16(16)); text("data"); number(bytes)
        return data
    }

    // Some platform decoders tolerate a truncated WAV by reducing its duration.
    // Validate declared chunk extents without loading the entire source into memory.
    private static func validateWaveContainer(_ input: URL) throws {
        let file = try FileHandle(forReadingFrom: input)
        defer { try? file.close() }
        let size = try file.seekToEnd()
        try file.seek(toOffset: 0)
        let prefix = try file.read(upToCount: 12) ?? Data()
        guard prefix.count == 12, String(decoding: prefix.prefix(4), as: UTF8.self) == "RIFF",
              String(decoding: prefix[8..<12], as: UTF8.self) == "WAVE" else { return }
        func u32(_ data: Data, _ offset: Int) -> UInt64 {
            (0..<4).reduce(UInt64(0)) { $0 | UInt64(data[offset + $1]) << ($1 * 8) }
        }
        let end = u32(prefix, 4) + 8
        guard end <= size, end >= 12 else { throw NativeFailure.invalid("WAV 파일이 잘렸습니다.") }
        var offset: UInt64 = 12
        while offset < end {
            guard end - offset >= 8 else { throw NativeFailure.invalid("WAV 청크가 잘렸습니다.") }
            try file.seek(toOffset: offset)
            let chunk = try file.read(upToCount: 8) ?? Data()
            guard chunk.count == 8 else { throw NativeFailure.invalid("WAV 청크가 잘렸습니다.") }
            let length = u32(chunk, 4)
            offset += 8 + length + length % 2
            guard offset <= end else { throw NativeFailure.invalid("WAV 데이터가 잘렸습니다.") }
        }
    }
}
