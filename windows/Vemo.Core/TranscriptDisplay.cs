using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Vemo.Core;

public sealed record TranscriptSegment(double StartSeconds, double EndSeconds, string Text);
public sealed record TranscriptDocument(string Text, IReadOnlyList<TranscriptSegment> Segments);
public sealed record TranscriptRow(string Text, double? StartSeconds, double? EndSeconds);

public static class TranscriptDisplay
{
    public const int MaximumCharacters = 320;
    internal static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)));
    public static void Validate(string text, IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count is 0 or > 100000) throw new InvalidDataException("Invalid transcript segment count.");
        var joined = new StringBuilder(); double start = 0, end = 0;
        foreach (var segment in segments)
        {
            if (segment.Text == null || !double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds) || segment.StartSeconds < start || segment.EndSeconds < end || segment.EndSeconds < segment.StartSeconds || segment.EndSeconds > 604800) throw new InvalidDataException("Invalid transcript timing.");
            start = segment.StartSeconds; end = segment.EndSeconds; joined.Append(segment.Text);
            if (joined.Length > 4 * 1024 * 1024) throw new InvalidDataException("Transcript timing exceeds limit.");
        }
        if (Normalize(text).Length == 0 || Normalize(text) != Normalize(joined.ToString())) throw new InvalidDataException("Transcript timing does not cover the exact transcript.");
    }
    public static IReadOnlyList<TranscriptRow> Rows(TranscriptDocument document)
    {
        var texts = Chunks(document.Text); var plain = texts.Select(x => new TranscriptRow(x, null, null)).ToArray();
        try { Validate(document.Text, document.Segments); } catch (Exception e) when (e is InvalidDataException or ArgumentException) { return plain; }
        var normalized = Normalize(document.Text);
        if (string.Concat(texts.Select(Normalize)) != normalized || string.Concat(document.Segments.Select(x => Normalize(x.Text))) != normalized) return plain;
        var owners = new List<int>();
        for (int i = 0; i < document.Segments.Count; i++) owners.AddRange(Enumerable.Repeat(i, Normalize(document.Segments[i].Text).Length));
        var result = new List<TranscriptRow>(); int consumed = 0;
        foreach (var text in texts)
        {
            var count = Normalize(text).Length;
            result.Add(count == 0 ? new(text, null, null) : new(text, document.Segments[owners[consumed]].StartSeconds, document.Segments[owners[consumed + count - 1]].EndSeconds)); consumed += count;
        }
        return result;
    }
    static IReadOnlyList<string> Chunks(string text)
    {
        var rows = new List<string>(); int start = 0;
        while (start < text.Length)
        {
            var limit = Math.Min(text.Length, start + MaximumCharacters); var end = limit;
            // Keep surrogate pairs together. Every original character and whitespace survives projection.
            if (limit < text.Length && char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit])) limit--;
            end = limit;
            for (int i = start; i < limit; i++)
            {
                if (text[i] == '\n' || (".!?。！？".Contains(text[i]) && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))))
                { end = i + 1; while (end < limit && char.IsWhiteSpace(text[end])) end++; break; }
            }
            if (end == limit && limit < text.Length)
            {
                var space = text.LastIndexOfAny([' ', '\t', '\r', '\n'], limit - 1, limit - start);
                if (space >= start) end = space + 1;
            }
            rows.Add(text[start..end]); start = end;
        }
        return rows;
    }
}

public static class WhisperTranscript
{
    public static IReadOnlyList<TranscriptSegment> Parse(string json, string text)
    {
        try
        {
            using var doc = JsonFormat.Strict(json); var segments = new List<TranscriptSegment>();
            foreach (var s in doc.RootElement.GetProperty("transcription").EnumerateArray())
            {
                var offsets = s.GetProperty("offsets"); var from = offsets.GetProperty("from").GetInt64(); var to = offsets.GetProperty("to").GetInt64();
                segments.Add(new(from / 1000d, to / 1000d, s.GetProperty("text").GetString()!));
                if (segments.Count > 100000) throw new InvalidDataException("Too many whisper segments.");
            }
            TranscriptDisplay.Validate(text, segments); return segments;
        }
        catch (Exception e) when (e is not (InvalidDataException or OutOfMemoryException)) { throw new InvalidDataException("Invalid whisper timestamp output."); }
    }
}

public static class TranscriptTimingFile
{
    public const string FileName = "transcript.timings.json";
    sealed record Sidecar(int SchemaVersion, string TextSha256, IReadOnlyList<TranscriptSegment> Segments);
    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Serialize(string text, IReadOnlyList<TranscriptSegment> segments)
    {
        TranscriptDisplay.Validate(text, segments); var json = JsonSerializer.Serialize(new Sidecar(1, Hash(text), segments));
        if (Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw new InvalidDataException("Transcript timing exceeds limit."); return json;
    }
    public static IReadOnlyList<TranscriptSegment> Read(string path, string text)
    {
        SafeFiles.NoLinks(path); if (!File.Exists(path)) return [];
        try
        {
            using var doc = JsonFormat.Strict(SafeFiles.Read(path)); var data = doc.Deserialize<Sidecar>() ?? throw new InvalidDataException();
            if (data.SchemaVersion != 1 || data.TextSha256 != Hash(text) || data.Segments == null) return [];
            TranscriptDisplay.Validate(text, data.Segments); return data.Segments;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException or InvalidOperationException or ArgumentException or NullReferenceException) { return []; }
    }
}
