import Foundation

/// Optional mobile presentation sidecar. The strict v1 result remains unchanged.
struct TranscriptTimeline: Encodable {
    static let filename = "transcript-timeline.json"
    static let keys: Set<String> = ["schema_version", "recording_id", "audio_sha256", "processor_id", "transcript_sha256", "segments"]
    let schema_version = 1
    let recording_id: String
    let audio_sha256: String
    let processor_id: String
    let transcript_sha256: String
    let segments: [Segment]

    struct Segment: Encodable {
        let start: Double?
        let end: Double?
        let text: String
        private enum CodingKeys: String, CodingKey { case start, end, text }
        func encode(to encoder: Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            // Null is part of the wire contract, rather than an omitted key.
            try container.encode(start, forKey: .start)
            try container.encode(end, forKey: .end)
            try container.encode(text, forKey: .text)
        }
    }

    init(recordingID: String, audioSHA256: String, processorID: String, transcript: Transcript) {
        recording_id = recordingID
        audio_sha256 = audioSHA256
        processor_id = processorID
        transcript_sha256 = MobileSyncValidation.digest(Data(transcript.text.utf8))
        segments = TranscriptDisplay.rows(transcript).map {
            Segment(start: $0.timing?.start_s, end: $0.timing?.end_s, text: $0.text)
        }
    }
}
