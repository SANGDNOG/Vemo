import Foundation
import AVFoundation
import NativeCore

func playbackChecks() throws {
    func fixture(audio: String?, pcm: String? = nil, includePCM: Bool = true) throws -> Job {
        var payload: [String: Any] = ["id": "synthetic-job", "uuid": "synthetic-memo", "status": "transcribing", "stage": "transcribing", "elapsed_seconds": 0, "title_status": "pending", "alignment_status": "pending"]
        payload["audio_path"] = audio
        if includePCM { payload["pcm_path"] = pcm ?? NSNull() }
        return try JSONDecoder().decode(Job.self, from: JSONSerialization.data(withJSONObject: payload))
    }
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("PlaybackChecks-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: root) }
    let qta = root.appendingPathComponent("original.qta").path
    let pcm = root.appendingPathComponent("prepared.wav").path
    let job = try fixture(audio: qta, pcm: pcm)
    try check(job.pcm_path == pcm && job.playback_path == pcm, "QTA consumer selects decoded prepared PCM")
    try check(job.audio_path == qta, "PCM playback preserves original audio_path")
    try check(try fixture(audio: root.appendingPathComponent("original.QTA").path, pcm: pcm).playback_path == pcm, "QTA extension selection is case insensitive")
    let pending = try fixture(audio: qta)
    try check(pending.pcm_path == nil && pending.playback_path == nil, "QTA with absent PCM waits without attempting unsupported original")
    try check(try fixture(audio: qta, pcm: "").playback_path == nil, "QTA with empty PCM path waits")
    let older = try fixture(audio: qta, includePCM: false)
    try check(older.pcm_path == nil && older.playback_path == nil && older.audio_path == qta, "older QTA jobs decode missing pcm_path and wait gracefully")
    let m4a = root.appendingPathComponent("original.m4a").path
    try check(try fixture(audio: m4a, pcm: pcm).playback_path == m4a, "M4A consumer retains original despite available PCM")
    try check(try fixture(audio: m4a, includePCM: false).playback_path == m4a, "older M4A jobs decode and retain original playback")
    try check(try fixture(audio: nil, pcm: pcm).playback_path == nil, "missing original source does not select unrelated PCM")

    // Exercise the same chosen-path ownership gate and AVAudioPlayer consumer with synthetic PCM.
    // Leave the QTA path nonexistent, proving playback does not read the original container.
    let format = AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1)!
    let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 16_000)!
    buffer.frameLength = 16_000
    buffer.floatChannelData![0].initialize(repeating: 0, count: 16_000)
    do {
        let file = try AVAudioFile(forWriting: URL(fileURLWithPath: pcm), settings: format.settings)
        try file.write(from: buffer)
    }
    guard let selectedPath = job.playback_path else { throw NativeFailure.invalid("FAIL: PCM playback path missing") }
    let owned = try Validation.owned(selectedPath, root: root)
    let player = try AVAudioPlayer(data: Data(contentsOf: owned))
    try check(abs(player.duration - 1) < 0.01, "QTA chosen owned synthetic mono16k WAV opens in AVAudioPlayer without original")
    let outside = try fixture(audio: qta, pcm: root.appendingPathComponent("../outside.wav").path)
    try rejects("QTA chosen PCM outside owned root rejected") { _ = try Validation.owned(outside.playback_path!, root: root) }
    let link = root.appendingPathComponent("linked.wav")
    try FileManager.default.createSymbolicLink(at: link, withDestinationURL: owned)
    let linked = try fixture(audio: qta, pcm: link.path)
    try rejects("QTA chosen symlink PCM rejected") { _ = try Validation.owned(linked.playback_path!, root: root) }
}
