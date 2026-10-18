import Foundation

/// Optional metadata; callers discard failures and retain the ordinary transcript.
public struct SpeakerDocument: Codable, Sendable {
    public struct Speaker: Codable, Sendable, Identifiable {
        public let id: String
        public let name: String
    }
    public struct Turn: Codable, Sendable {
        public let start_s: Double
        public let end_s: Double
        public let speaker_id: String
    }
    public struct Segment: Codable, Sendable {
        public let start_s: Double
        public let end_s: Double
        public let text: String
        public let speaker_id: String?
        private enum CodingKeys: String, CodingKey { case start_s, end_s, text, speaker_id }
        public func encode(to encoder: Encoder) throws {
            var container = encoder.container(keyedBy: CodingKeys.self)
            try container.encode(start_s, forKey: .start_s)
            try container.encode(end_s, forKey: .end_s)
            try container.encode(text, forKey: .text)
            try container.encode(speaker_id, forKey: .speaker_id)
        }
    }
    public let schema_version: Int
    public let audio_sha256: String
    public let transcript_sha256: String
    public let generation: String
    public let speakers: [Speaker]
    public let turns: [Turn]
    public let segments: [Segment]
    public static let filename = "speakers.json"
    static let keys: Set<String> = ["schema_version", "audio_sha256", "transcript_sha256", "generation", "speakers", "turns", "segments"]

    public static func decode(_ data: Data, audioHash: String, transcript: Transcript) throws -> Self {
        guard data.count <= WireCodec.limit else { throw NativeFailure.invalid("화자 정보가 너무 큽니다.") }
        // Reuse the bounded recursive pass, including escaped duplicate keys.
        try MobileSyncValidation.object(data, keys: keys)
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        for (name, expected) in [("speakers", Set(["id", "name"])),
                                 ("turns", Set(["start_s", "end_s", "speaker_id"])),
                                 ("segments", Set(["start_s", "end_s", "text", "speaker_id"]))] {
            guard let items = object?[name] as? [[String: Any]], items.count <= 100_000,
                  items.allSatisfy({ Set($0.keys) == expected }) else {
                throw NativeFailure.invalid("화자 정보 필드가 올바르지 않습니다.")
            }
        }
        let doc = try JSONDecoder().decode(Self.self, from: data)
        func validTime(_ start: Double, _ end: Double) -> Bool {
            start.isFinite && end.isFinite && start >= 0 && start <= end && end <= 3600
        }
        guard doc.schema_version == 1, doc.audio_sha256 == audioHash,
              audioHash.range(of: "^[0-9a-f]{64}$", options: .regularExpression) != nil,
              doc.transcript_sha256 == MobileSyncValidation.digest(Data(transcript.text.utf8)),
              UUID(uuidString: doc.generation)?.uuidString.lowercased() == doc.generation,
              doc.speakers.count <= 100_000, doc.turns.count <= 100_000, doc.segments.count <= 100_000 else {
            throw NativeFailure.invalid("현재 녹음과 일치하지 않는 화자 정보입니다.")
        }
        let ids = Set(doc.speakers.map(\.id))
        guard ids.count == doc.speakers.count, doc.speakers.allSatisfy({ speaker in
            speaker.id.range(of: "^speaker_[1-9][0-9]*$", options: .regularExpression) != nil &&
            (1...80).contains(speaker.name.unicodeScalars.count) &&
            speaker.name == speaker.name.trimmingCharacters(in: .whitespacesAndNewlines) &&
            !speaker.name.unicodeScalars.contains(where: { scalar in
                switch scalar.properties.generalCategory {
                case .control, .format, .surrogate, .privateUse, .unassigned: return true
                default: return false
                }
            })
        }) else { throw NativeFailure.invalid("유효하지 않은 화자 이름입니다.") }
        var previous = -1.0
        for turn in doc.turns {
            guard validTime(turn.start_s, turn.end_s), turn.start_s >= previous, ids.contains(turn.speaker_id) else {
                throw NativeFailure.invalid("유효하지 않은 화자 구간입니다.")
            }
            previous = turn.start_s
        }
        previous = -1
        var previousEnd = -1.0
        for segment in doc.segments {
            guard validTime(segment.start_s, segment.end_s), segment.start_s >= previous,
                  segment.end_s >= previousEnd,
                  segment.speaker_id.map({ ids.contains($0) }) ?? true else {
                throw NativeFailure.invalid("유효하지 않은 화자 전사입니다.")
            }
            previous = segment.start_s
            previousEnd = segment.end_s
        }
        guard normalized(doc.segments.map(\.text).joined()) == normalized(transcript.text) else {
            throw NativeFailure.invalid("화자 전사가 원문과 다릅니다.")
        }
        try doc.validateAssignments()
        return doc
    }

    private func validateAssignments() throws {
        struct Event { let time: Double; let id: String; let delta: Int }
        struct Span { let start: Double; let end: Double; let id: String? }
        var events: [Event] = []
        for turn in turns where turn.end_s > turn.start_s {
            events.append(Event(time: turn.start_s, id: turn.speaker_id, delta: 1))
            events.append(Event(time: turn.end_s, id: turn.speaker_id, delta: -1))
        }
        events.sort { $0.time < $1.time }
        var active: [String: Int] = [:]
        var spans: [Span] = []
        var index = 0
        var previous = events.first?.time ?? 0
        while index < events.count {
            let time = events[index].time
            if time > previous, !active.isEmpty {
                spans.append(Span(start: previous, end: time, id: active.count == 1 ? active.keys.first : nil))
            }
            while index < events.count, events[index].time == time {
                let event = events[index]
                let count = (active[event.id] ?? 0) + event.delta
                if count == 0 { active.removeValue(forKey: event.id) } else { active[event.id] = count }
                index += 1
            }
            previous = time
        }
        var first = 0
        var operations = 0
        for segment in segments {
            guard let claimed = segment.speaker_id else { continue }
            while first < spans.count, spans[first].end <= segment.start_s { first += 1 }
            var cursor = first
            var covered = 0.0
            while cursor < spans.count, spans[cursor].start < segment.end_s {
                operations += 1
                guard operations <= 2_000_000 else { throw NativeFailure.invalid("화자 구간이 너무 복잡합니다.") }
                let span = spans[cursor]
                let overlap = min(segment.end_s, span.end) - max(segment.start_s, span.start)
                if overlap > 0 {
                    guard span.id == claimed else { throw NativeFailure.invalid("화자가 겹치는 구간은 배정할 수 없습니다.") }
                    covered += overlap
                }
                cursor += 1
            }
            guard covered > (segment.end_s - segment.start_s) / 2 else {
                throw NativeFailure.invalid("화자 배정의 시간 근거가 부족합니다.")
            }
        }
    }

    static func normalized(_ text: String) -> String {
        String(text.precomposedStringWithCanonicalMapping.filter { c in
            !c.unicodeScalars.allSatisfy { CharacterSet.whitespacesAndNewlines.contains($0) || CharacterSet.punctuationCharacters.contains($0) }
        })
    }

    public static func load(job: Job, root: URL, transcript: Transcript) throws -> Self? {
        guard let path = job.speakers_path, let audioPath = job.audio_path else { return nil }
        let artifactRoot = root.appendingPathComponent("artifacts/" + (try Validation.component(job.id)))
        let sidecar = try Validation.owned(path, root: artifactRoot)
        let audio = try Validation.owned(audioPath, root: root)
        return try decode(BrokerFiles.read(sidecar), audioHash: BrokerFiles.hash(audio), transcript: transcript)
    }

    /// Preserve sentence rows and every original character. Mixed speakers remain unlabeled.
    public func speakerID(for row: TranscriptDisplay.Row) -> String? {
        guard let timing = row.timing, timing.end_s > timing.start_s else { return nil }
        let matching = segments.filter { $0.start_s < timing.end_s && $0.end_s > timing.start_s }
        guard let id = matching.first?.speaker_id, matching.allSatisfy({ $0.speaker_id == id }) else { return nil }
        return id
    }
}
