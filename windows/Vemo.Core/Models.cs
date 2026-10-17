using System.Text.Json;
using System.Text.Json.Serialization;
namespace Vemo.Core;

public enum JobState { Queued, Transcribing, GeneratingNotes, Completed, Failed, Cancelled, Purged }
public sealed record ModelSelection
{
    public string WhisperProfile { get; init; } = "large-v3-turbo";
    public string NotesProfile { get; init; } = "Qwen3.5 9B GGUF";
    public string WhisperExecutable { get; init; } = "";
    public string WhisperModel { get; init; } = "";
    public string LlamaExecutable { get; init; } = "";
    public string LlamaModel { get; init; } = "";
    public string FfmpegExecutable { get; init; } = "";
    public string Compute { get; init; } = "CPU";
    public bool CudaConfirmed { get; init; }
    public int GpuLayers { get; init; } = 20;
    public int TimeoutMinutes { get; init; } = 120;
}
// Pin metadata is supplied by the release owner. No entry here authorizes downloads.
public sealed record ModelCatalogueEntry(string Id, string DisplayName, string Repository, string Revision, string FileName, string Sha256, string License, string? MemoryEstimate);
public sealed record AppSettings
{
    public string OutputDirectory { get; init; } = "";
    public string MobileDirectory { get; init; } = "";
    public bool QueuePaused { get; init; }
    public ModelSelection Models { get; init; } = new();
}
public sealed record LibraryFolder(string Id, string Name, string? ParentId);
public sealed record MobileOrigin(string SyncRoot, string RecordingId, string AudioSha256, string ProcessorId, string RecordedAt, string OriginalTitle);
public sealed record RecordingJob
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? RecordedAt { get; init; }
    public string Extension { get; init; } = ".wav";
    public required string AudioSha256 { get; init; }
    public string? FolderId { get; init; }
    public bool Trashed { get; init; }
    public JobState State { get; init; } = JobState.Queued;
    public string? Error { get; init; }
    public ModelSelection Models { get; init; } = new();
    public string? PendingArtifactRevision { get; init; }
    public string? ArtifactRevision { get; init; }
    public string? ExportPath { get; init; }
    public string? ExportError { get; init; }
    public MobileOrigin? Mobile { get; init; }
}
internal sealed class LibraryData
{
    public int SchemaVersion { get; set; } = 1;
    public string ProcessorId { get; set; } = Guid.NewGuid().ToString();
    public AppSettings Settings { get; set; } = new();
    public List<RecordingJob> Jobs { get; set; } = [];
    public List<LibraryFolder> Folders { get; set; } = [];
}
public static class JsonFormat
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static JsonDocument Strict(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 4 * 1024 * 1024) throw new InvalidDataException("JSON exceeds 4 MiB.");
        var doc = JsonDocument.Parse(json, new() { MaxDepth = 24 });
        try { Check(doc.RootElement); return doc; } catch { doc.Dispose(); throw; }
    }
    static void Check(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { var keys = new HashSet<string>(); foreach (var p in e.EnumerateObject()) { if (!keys.Add(p.Name)) throw new InvalidDataException("Duplicate JSON property."); Check(p.Value); } }
        if (e.ValueKind == JsonValueKind.Array) foreach (var v in e.EnumerateArray()) Check(v);
    }
}
