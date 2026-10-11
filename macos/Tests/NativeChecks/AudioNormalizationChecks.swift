import Foundation
import AVFoundation
import NativeCore

func audioNormalizationChecks() async throws {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("Vemo audio checks " + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let source = root.appendingPathComponent("stereo input.wav")
    let format = AVAudioFormat(standardFormatWithSampleRate: 48000, channels: 2)!
    do {
    var settings = format.settings; settings[AVLinearPCMIsNonInterleaved] = false
    let file = try AVAudioFile(forWriting: source, settings: settings)
    // Multiple buffers catch implementations that convert only the first read.
    let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 48000)!
    buffer.frameLength = 48000
    for i in 0..<48000 {
        buffer.floatChannelData![0][i] = 0.5
        buffer.floatChannelData![1][i] = 0.25
    }
    for _ in 0..<3 { try file.write(from: buffer) }
    }
    // Close the writer before readers inspect its finalized container.
    try await audioOutputOwnershipChecks(root: root, validInput: source)
    let output = root.appendingPathComponent("normalized output.wav")
    try await AudioNormalization.normalize(input: source, output: output)
    let data = try Data(contentsOf: output)
    func u16(_ offset: Int) -> Int { Int(data[offset]) | Int(data[offset + 1]) << 8 }
    func u32(_ offset: Int) -> Int { u16(offset) | u16(offset + 2) << 16 }
    try check(String(decoding: data.prefix(4), as: UTF8.self) == "RIFF" && String(decoding: data[8..<12], as: UTF8.self) == "WAVE", "standard WAV header")
    try check(u16(20) == 1 && u16(22) == 1 && u16(34) == 16 && u32(24) == 16000, "mono 16k signed16 PCM")
    print("Observed normalized frames: \(u32(40) / 2), bytes: \(data.count)")
    try check(u32(40) / 2 == 48000 && data.count == 44 + 96000, "full three-second resampled duration")
    let sample = Int(Int16(bitPattern: UInt16(u16(44 + 16000))))
    try check(sample > 8000 && sample < 20000, "stereo downmix retains signal")
    let compressed = root.appendingPathComponent("synthetic AAC.m4a")
    do {
        let encoder = try AVAudioFile(forWriting: compressed, settings: [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 128000])
        let chunk = AVAudioPCMBuffer(pcmFormat: encoder.processingFormat, frameCapacity: 48000)!
        chunk.frameLength = 48000
        for channel in 0..<2 { for i in 0..<48000 { chunk.floatChannelData![channel][i] = Float(sin(Double(i) * 2 * .pi * 440 / 48000)) * 0.25 } }
        for _ in 0..<3 { try encoder.write(from: chunk) }
    }
    try FileManager.default.removeItem(at: output)
    try await AudioNormalization.normalize(input: compressed, output: output)
    let aacPCM = try AVAudioFile(forReading: output)
    try check(aacPCM.length == 48000, "AAC decodes full three-second duration")
    let partialAAC = root.appendingPathComponent("truncated AAC.m4a")
    let encodedAAC = try Data(contentsOf: compressed)
    try encodedAAC.dropLast(100).write(to: partialAAC)
    try FileManager.default.removeItem(at: output)
    do { try await AudioNormalization.normalize(input: partialAAC, output: output); throw NativeFailure.invalid("FAIL: truncated AAC accepted") }
    catch { try check(!error.localizedDescription.contains("FAIL:"), "truncated AAC rejected") }
    let malformed = root.appendingPathComponent("malformed.wav")
    try Data("not audio".utf8).write(to: malformed)
    do { try await AudioNormalization.normalize(input: malformed, output: output); throw NativeFailure.invalid("FAIL: malformed accepted") }
    catch { try check(!error.localizedDescription.contains("FAIL:"), "malformed rejected") }
    try check(!FileManager.default.fileExists(atPath: output.path), "failed decode cleans output")
    let truncated = root.appendingPathComponent("truncated.wav")
    let original = try Data(contentsOf: source)
    try original.prefix(original.count / 2).write(to: truncated)
    do { try await AudioNormalization.normalize(input: truncated, output: output); throw NativeFailure.invalid("FAIL: truncated accepted") }
    catch { try check(!error.localizedDescription.contains("FAIL:"), "truncated WAV rejected") }
    try check(!FileManager.default.fileExists(atPath: output.path), "truncated decode leaves no PCM")
}


private func audioOutputOwnershipChecks(root: URL, validInput: URL) async throws {
    let manager = FileManager.default
    let validBytes = try Data(contentsOf: validInput)
    let sentinel = Data("existing output must survive".utf8)
    var results: [(Bool, String)] = []
    for malformed in [true, false] {
        for kind in ["file", "directory", "symlink", "hardlink", "dangling symlink"] {
            let folder = root.appendingPathComponent("ownership-\(malformed)-\(kind)")
            try manager.createDirectory(at: folder, withIntermediateDirectories: true)
            let input = folder.appendingPathComponent("input.wav")
            let original = malformed ? Data("malformed input".utf8) : validBytes
            try original.write(to: input)
            let output = folder.appendingPathComponent("existing output")
            let child = output.appendingPathComponent("sentinel")
            switch kind {
            case "file": try sentinel.write(to: output)
            case "directory":
                try manager.createDirectory(at: output, withIntermediateDirectories: true)
                try sentinel.write(to: child)
            case "symlink": try manager.createSymbolicLink(at: output, withDestinationURL: input)
            case "hardlink": try manager.linkItem(at: input, to: output)
            default: try manager.createSymbolicLink(atPath: output.path, withDestinationPath: "missing target")
            }
            let identityBefore = try manager.attributesOfItem(atPath: output.path)[.systemFileNumber] as? NSNumber
            var rejected = false
            do { try await AudioNormalization.normalize(input: input, output: output) } catch { rejected = true }
            let preserved: Bool
            switch kind {
            case "file": preserved = (try? Data(contentsOf: output)) == sentinel
            case "directory": preserved = (try? Data(contentsOf: child)) == sentinel
            case "symlink": preserved = (try? manager.destinationOfSymbolicLink(atPath: output.path)) == input.path
            case "hardlink": preserved = (try? Data(contentsOf: output)) == original
            default: preserved = (try? manager.destinationOfSymbolicLink(atPath: output.path)) == "missing target"
            }
            let identityAfter = (try? manager.attributesOfItem(atPath: output.path))?[.systemFileNumber] as? NSNumber
            let sourceUnchanged = (try? Data(contentsOf: input)) == original
            let passed = rejected && preserved && sourceUnchanged && identityBefore == identityAfter
            let name = "\(malformed ? "malformed" : "valid") input rejects existing \(kind) without mutation"
            print("OBSERVED: \(name): \(passed ? "preserved" : "FAILED")")
            results.append((passed, name))
        }
    }
    for (passed, name) in results { try check(passed, name) }
}
