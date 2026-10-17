using System.Text.Json;
using System.Text.RegularExpressions;
namespace Vemo.Core;

public sealed record MobileIssue(string Path, string Message);
public sealed record MobileScan(int Imported, int Skipped, IReadOnlyList<MobileIssue> Issues);
public sealed class MobileFolderAdapter(LibraryStore store)
{
    readonly SemaphoreSlim gate = new(1, 1);
    static readonly string[] RequestKeys = ["schema_version", "recording_id", "audio_sha256", "audio_bytes", "audio_file", "recorded_at", "title", "mode", "target_processor_id", "completed"];
    public void Register(string directory)
    {
        directory = SafeFiles.Full(directory); SafeFiles.Disjoint(store.Root, directory); if (store.Settings.OutputDirectory != "") SafeFiles.Disjoint(directory, store.Settings.OutputDirectory);
        SafeFiles.NoLinks(directory); Directory.CreateDirectory(directory); var marker = Path.Combine(directory, "processor.json");
        if (File.Exists(marker)) Owner(directory); else SafeFiles.Atomic(marker, JsonSerializer.Serialize(new { schema_version = 1, processor_id = store.ProcessorId, display_name = "Vemo Windows" }), false);
        store.UpdateSettings(store.Settings with { MobileDirectory = directory });
    }
    void Owner(string dir)
    {
        using var doc = JsonFormat.Strict(SafeFiles.Read(Path.Combine(dir, "processor.json"))); var p = doc.RootElement;
        NotesDocument.Keys(p, "schema_version", "processor_id", "display_name");
        if (p.GetProperty("schema_version").GetInt32() != 1 || p.GetProperty("processor_id").GetString() != store.ProcessorId) throw new InvalidDataException("Sync folder belongs to another processor.");
    }
    static void Uuid(string s) { if (!Guid.TryParseExact(s, "D", out var id) || id.ToString() != s) throw new InvalidDataException("Invalid mobile UUID."); }
    static void Hash(string s) { if (!Regex.IsMatch(s, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid mobile hash."); }
    static (MobileOrigin Origin, long Bytes, string Mode, bool Completed) Request(string dir, string processor, string root)
    {
        using var doc = JsonFormat.Strict(SafeFiles.Read(Path.Combine(dir, "recording.json"))); var p = doc.RootElement; NotesDocument.Keys(p, RequestKeys);
        string S(string key) => p.GetProperty(key).GetString() ?? throw new InvalidDataException("Missing " + key);
        var id = S("recording_id"); Uuid(id); var hash = S("audio_sha256"); Hash(hash); var date = S("recorded_at"); SafeFiles.RecordedAt(date); var title = S("title"); SafeFiles.Title(title);
        var mode = S("mode"); var target = p.GetProperty("target_processor_id"); if (target.ValueKind != JsonValueKind.Null) Uuid(target.GetString()!);
        if (p.GetProperty("schema_version").GetInt32() != 1 || S("audio_file") != "audio.m4a" || mode is not ("desktop" or "record_only")) throw new InvalidDataException("Unsupported recording request.");
        var bytes = p.GetProperty("audio_bytes").GetInt64(); if (bytes is <= 0 or > 2147483648) throw new InvalidDataException("Invalid mobile audio size.");
        if (Path.GetFileName(dir) != hash || Path.GetFileName(Path.GetDirectoryName(dir)) != id) throw new InvalidDataException("Mobile path identity mismatch.");
        if (mode == "desktop" && (target.ValueKind != JsonValueKind.String || target.GetString() != processor)) throw new InvalidDataException("Request targets another processor.");
        return (new(root, id, hash, processor, date, title), bytes, mode, p.GetProperty("completed").GetBoolean());
    }
    public async Task<MobileScan> ScanAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct); try
        {
            var root = store.Settings.MobileDirectory; if (string.IsNullOrWhiteSpace(root)) return new(0, 0, []); Owner(root);
            var issues = new List<MobileIssue>(); int imported = 0, skipped = 0; var recordings = Path.Combine(root, "recordings"); SafeFiles.NoLinks(recordings); if (!Directory.Exists(recordings)) return new(0, 0, []);
            foreach (var idDir in Directory.EnumerateDirectories(recordings).Take(10000))
            {
                ct.ThrowIfCancellationRequested(); try
                {
                    SafeFiles.NoLinks(idDir); Uuid(Path.GetFileName(idDir));
                    foreach (var dir in Directory.EnumerateDirectories(idDir).Take(1000))
                    {
                        ct.ThrowIfCancellationRequested(); try
                        {
                            SafeFiles.NoLinks(dir); Hash(Path.GetFileName(dir)); var marker = Path.Combine(dir, "recording.json"); if (!File.Exists(marker)) { skipped++; continue; }
                            var before = SafeFiles.Read(marker); var request = Request(dir, store.ProcessorId, root);
                            if (!request.Completed || request.Mode != "desktop") { skipped++; continue; }
                            var existing = store.Jobs.FirstOrDefault(j => j.Mobile != null && LibraryStore.SameMobileRecording(j.Mobile, request.Origin));
                            if (existing != null) { if (existing.Mobile != request.Origin) throw new InvalidDataException("Mobile identity changed after import."); skipped++; continue; }
                            var audio = Path.Combine(dir, "audio.m4a"); SafeFiles.NoLinks(audio);
                            if (new FileInfo(audio).Length != request.Bytes || SafeFiles.Hash(audio) != request.Origin.AudioSha256) throw new InvalidDataException("Mobile audio hash/size mismatch.");
                            if (before != SafeFiles.Read(marker)) throw new IOException("Mobile request is changing; retry after sync completes.");
                            store.Import(audio, request.Origin.OriginalTitle, request.Origin.RecordedAt, request.Origin); imported++;
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { issues.Add(new(dir, e.Message)); }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { issues.Add(new(idDir, e.Message)); }
            }
            return new(imported, skipped, issues);
        }
        finally { gate.Release(); }
    }
    public void Publish(string id)
    {
        var j = store.Get(id); var m = j.Mobile ?? throw new InvalidOperationException("Not a mobile recording.");
        if (j.State != JobState.Completed || j.Trashed || m.SyncRoot != store.Settings.MobileDirectory) throw new InvalidOperationException("Mobile result is not ready or configured sync root changed."); Owner(m.SyncRoot);
        var requestDir = Path.Combine(m.SyncRoot, "recordings", m.RecordingId, m.AudioSha256); var marker = Path.Combine(requestDir, "recording.json"); var before = SafeFiles.Read(marker); var request = Request(requestDir, store.ProcessorId, m.SyncRoot);
        if (request.Origin != m || !request.Completed || request.Mode != "desktop" || j.RecordedAt != m.RecordedAt || SafeFiles.Hash(store.AudioPath(id)) != m.AudioSha256) throw new InvalidDataException("Mobile request identity changed; refusing stale result.");
        var transcript = store.ReadTranscript(id); var notes = store.ReadNotes(id);
        if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(notes)) throw new InvalidDataException("Mobile publication requires nonempty transcript and notes artifacts.");
        var json = JsonSerializer.Serialize(new { schema_version = 1, recording_id = m.RecordingId, audio_sha256 = m.AudioSha256, processor_id = m.ProcessorId, title = j.Title, recorded_at = m.RecordedAt, transcript, notes_markdown = notes }, JsonFormat.Options);
        var dest = Path.Combine(m.SyncRoot, "results", m.RecordingId, m.AudioSha256, "result.json"); SafeFiles.NoLinks(dest);
        var audio = Path.Combine(requestDir, "audio.m4a"); SafeFiles.NoLinks(audio);
        if (new FileInfo(audio).Length != request.Bytes || SafeFiles.Hash(audio) != m.AudioSha256) throw new InvalidDataException("Remote mobile audio hash/size changed; refusing stale result.");
        if (before != SafeFiles.Read(marker)) throw new IOException("Mobile request is changing; retry after sync completes.");
        if (File.Exists(dest)) { if (SafeFiles.Read(dest) == json) return; throw new IOException("A different result already exists; preserving it."); }
        SafeFiles.Atomic(dest, json, false);
    }
}
