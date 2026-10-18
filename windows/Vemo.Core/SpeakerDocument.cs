using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
namespace Vemo.Core;

public sealed record SpeakerTurn(double StartSeconds, double EndSeconds, string SpeakerId);
public sealed record SpeakerIdentity(string Id, string Name);
public sealed record SpeakerSegment(double StartSeconds, double EndSeconds, string Text, string? SpeakerId);

// Optional, recording-scoped metadata. Never embedded in or used to rewrite ASR.
public sealed class SpeakerDocument
{
    public const string FileName = "speakers.json";
    public const int MaximumBytes = 8 * 1024 * 1024;
    public string AudioSha256 { get; }
    public string TranscriptSha256 { get; }
    public string Generation { get; }
    public IReadOnlyList<SpeakerIdentity> Speakers { get; }
    public IReadOnlyList<SpeakerTurn> Turns { get; }
    public IReadOnlyList<SpeakerSegment> Segments { get; }
    SpeakerDocument(string audio, string transcript, string generation, SpeakerIdentity[] speakers, SpeakerTurn[] turns, SpeakerSegment[] segments)
    { AudioSha256 = audio; TranscriptSha256 = transcript; Generation = generation; Speakers = Array.AsReadOnly(speakers); Turns = Array.AsReadOnly(turns); Segments = Array.AsReadOnly(segments); }
    public static string TextHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static void Hash(string hash) { if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid speaker hash."); }
    static void Time(double start, double end) { if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start || end > 3600) throw new InvalidDataException("Speaker timing must be finite and within one hour."); }
    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.EnumerateRunes().Count() is < 1 or > 80 || name.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) || !WellFormed(name)) throw new InvalidDataException("화자 이름은 제어 문자 없이 1–80자여야 합니다.");
        return name;
    }
    static bool WellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++) if (char.IsSurrogate(text[i])) { if (!char.IsHighSurrogate(text[i]) || ++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
        return true;
    }
    public static SpeakerDocument Create(string audioHash, TranscriptDocument transcript, IReadOnlyList<SpeakerTurn> turns)
    {
        Hash(audioHash); TranscriptDisplay.Validate(transcript.Text, transcript.Segments);
        if (turns.Count > 100000) throw new InvalidDataException("Too many speaker turns.");
        foreach (var turn in turns) { Time(turn.StartSeconds, turn.EndSeconds); if (string.IsNullOrWhiteSpace(turn.SpeakerId)) throw new InvalidDataException("Missing cluster id."); }
        var ids = turns.OrderBy(t => t.StartSeconds).ThenBy(t => t.SpeakerId, StringComparer.Ordinal).Select(t => t.SpeakerId).Distinct().Select((id, i) => (id, canonical: "speaker_" + (i + 1))).ToDictionary(x => x.id, x => x.canonical);
        var ordered = turns.OrderBy(t => t.StartSeconds).ThenBy(t => t.EndSeconds).Select(t => t with { SpeakerId = ids[t.SpeakerId] }).ToArray();
        var assignments = new Assignments(ordered);
        var segments = transcript.Segments.Select(s => { Time(s.StartSeconds, s.EndSeconds); return new SpeakerSegment(s.StartSeconds, s.EndSeconds, s.Text, assignments.Assign(s.StartSeconds, s.EndSeconds)); }).ToArray();
        var result = new SpeakerDocument(audioHash, TextHash(transcript.Text), Guid.NewGuid().ToString(), ids.Values.Select((id, i) => new SpeakerIdentity(id, "화자 " + (i + 1))).ToArray(), ordered, segments);
        // Also enforce the encoded size before any caller may commit it.
        result.Serialize(); return result;
    }
    // Work only with original ASR segments. Even sequential coverage by two
    // speakers makes a coarse segment ambiguous; never invent word boundaries.
    sealed class Assignments
    {
        readonly IReadOnlyList<SpeakerTurn> turns;
        readonly double[] prefixEnd;
        int visits;
        public Assignments(IReadOnlyList<SpeakerTurn> turns)
        {
            this.turns = turns; prefixEnd = new double[turns.Count]; double maximum = 0;
            for (int i = 0; i < turns.Count; i++) { maximum = Math.Max(maximum, turns[i].EndSeconds); prefixEnd[i] = maximum; }
        }
        public string? Assign(double start, double end)
        {
            if (end <= start) return null;
            // Skip every prefix whose intervals have already ended. This keeps
            // ordinary disjoint recordings linear apart from binary searches.
            int low = 0, high = turns.Count;
            while (low < high) { int mid = low + (high - low) / 2; if (prefixEnd[mid] <= start) low = mid + 1; else high = mid; }
            string? owner = null; double coverage = 0, coveredEnd = start;
            for (int i = low; i < turns.Count && turns[i].StartSeconds < end; i++)
            {
                if (++visits > 2_000_000) throw new InvalidDataException("화자 구간이 너무 복잡합니다. 더 짧은 녹음을 사용하세요.");
                var turn = turns[i]; var from = Math.Max(start, turn.StartSeconds); var to = Math.Min(end, turn.EndSeconds);
                if (to <= from) continue;
                if (owner != null && owner != turn.SpeakerId) return null;
                owner = turn.SpeakerId; coverage += Math.Max(0, to - Math.Max(from, coveredEnd)); coveredEnd = Math.Max(coveredEnd, to);
            }
            return coverage > (end - start) / 2 ? owner : null;
        }
    }
    public SpeakerDocument Rename(string speakerId, string name)
    {
        name = ValidateName(name); if (!Speakers.Any(s => s.Id == speakerId)) throw new InvalidDataException("Unknown speaker.");
        return new(AudioSha256, TranscriptSha256, Generation, Speakers.Select(s => s.Id == speakerId ? s with { Name = name } : s).ToArray(), Turns.ToArray(), Segments.ToArray());
    }
    public string Serialize()
    {
        var json = JsonSerializer.Serialize(new { schema_version = 1, audio_sha256 = AudioSha256, transcript_sha256 = TranscriptSha256, generation = Generation,
            speakers = Speakers.Select(s => new { id = s.Id, name = s.Name }), turns = Turns.Select(t => new { start_s = t.StartSeconds, end_s = t.EndSeconds, speaker_id = t.SpeakerId }),
            segments = Segments.Select(s => new { start_s = s.StartSeconds, end_s = s.EndSeconds, text = s.Text, speaker_id = s.SpeakerId }) });
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Speaker document exceeds 8 MiB."); return json;
    }
    static void CheckKeys(JsonElement value, params string[] keys) => NotesDocument.Keys(value, keys);
    static void NoDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) { var keys = new HashSet<string>(); foreach (var p in value.EnumerateObject()) { if (!keys.Add(p.Name)) throw new InvalidDataException("Duplicate speaker JSON key."); NoDuplicates(p.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) NoDuplicates(child);
    }
    public static SpeakerDocument Parse(string json, string audioHash, string transcript)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Speaker document exceeds 8 MiB.");
            using var doc = JsonDocument.Parse(json, new() { MaxDepth = 16 }); var root = doc.RootElement; NoDuplicates(root);
            CheckKeys(root, "schema_version", "audio_sha256", "transcript_sha256", "generation", "speakers", "turns", "segments");
            string S(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new InvalidDataException("Null speaker field.");
            var audio = S(root, "audio_sha256"); var textHash = S(root, "transcript_sha256"); var generation = S(root, "generation"); Hash(audio); Hash(textHash);
            if (root.GetProperty("schema_version").GetInt32() != 1 || audio != audioHash || textHash != TextHash(transcript) || (!Guid.TryParseExact(generation, "D", out var parsedGeneration) || parsedGeneration.ToString() != generation)) throw new InvalidDataException("Stale or invalid speaker document.");
            var speakers = new List<SpeakerIdentity>(); var ids = new HashSet<string>();
            foreach (var s in root.GetProperty("speakers").EnumerateArray())
            {
                CheckKeys(s, "id", "name"); var id = S(s, "id"); var name = S(s, "name");
                if (!Regex.IsMatch(id, "^speaker_[1-9][0-9]*$") || !ids.Add(id) || ValidateName(name) != name || speakers.Count >= 100000) throw new InvalidDataException("Invalid speaker identity.");
                speakers.Add(new(id, name));
            }
            var turns = new List<SpeakerTurn>();
            foreach (var t in root.GetProperty("turns").EnumerateArray())
            {
                CheckKeys(t, "start_s", "end_s", "speaker_id"); var start = t.GetProperty("start_s").GetDouble(); var end = t.GetProperty("end_s").GetDouble(); var id = S(t, "speaker_id"); Time(start, end);
                if (!ids.Contains(id) || turns.Count >= 100000) throw new InvalidDataException("Invalid speaker turn."); turns.Add(new(start, end, id));
            }
            var ordered = turns.OrderBy(t => t.StartSeconds).ThenBy(t => t.EndSeconds).ToArray();
            var assignments = new Assignments(ordered); var segments = new List<SpeakerSegment>();
            foreach (var s in root.GetProperty("segments").EnumerateArray())
            {
                CheckKeys(s, "start_s", "end_s", "text", "speaker_id"); var start = s.GetProperty("start_s").GetDouble(); var end = s.GetProperty("end_s").GetDouble(); var text = S(s, "text"); var id = s.GetProperty("speaker_id").GetString(); Time(start, end);
                if (segments.Count >= 100000 || (id != null && (!ids.Contains(id) || assignments.Assign(start, end) != id))) throw new InvalidDataException("Invalid speaker segment assignment."); segments.Add(new(start, end, text, id));
            }
            TranscriptDisplay.Validate(transcript, segments.Select(s => new TranscriptSegment(s.StartSeconds, s.EndSeconds, s.Text)).ToArray());
            return new(audio, textHash, generation, speakers.ToArray(), turns.ToArray(), segments.ToArray());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException) { throw new InvalidDataException("Invalid speaker document.", e); }
    }
    public static SpeakerDocument? ReadOptional(string path, string audioHash, string transcript)
    {
        try { SafeFiles.NoLinks(path); if (!File.Exists(path)) return null; if (new FileInfo(path).Length > MaximumBytes) return null; return Parse(File.ReadAllText(path, new UTF8Encoding(false, true)), audioHash, transcript); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return null; }
    }
}
