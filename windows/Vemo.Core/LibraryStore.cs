using System.IO.Compression;
using System.Text.Json;
namespace Vemo.Core;

public sealed class LibraryStore : IDisposable
{
    readonly object gate = new(); readonly string root; readonly FileStream ownership; LibraryData data;
    public string Root => root;
    public LibraryStore(string root)
    {
        this.root = SafeFiles.Full(root); SafeFiles.NoLinks(this.root); Directory.CreateDirectory(this.root);
        var lockPath = Path.Combine(this.root, "library.lock"); SafeFiles.NoLinks(lockPath); ownership = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var p = Path.Combine(root, "library.json");
            if (File.Exists(p)) { using var doc = JsonFormat.Strict(SafeFiles.Read(p)); data = doc.Deserialize<LibraryData>(JsonFormat.Options) ?? throw new InvalidDataException("Invalid library."); }
            else data = new();
            if (data.SchemaVersion != 1) throw new InvalidDataException("Unsupported library version.");
            foreach (var j in data.Jobs) { ValidateId(j.Id); SafeFiles.Title(j.Title); SafeFiles.RecordedAt(j.RecordedAt); if (j.ArtifactRevision != null) ValidateId(j.ArtifactRevision); if (j.PendingArtifactRevision != null) { ValidateId(j.PendingArtifactRevision); if (j.PendingArtifactRevision == j.ArtifactRevision) throw new InvalidDataException("Pending artifacts must not overwrite the committed revision."); } }
            foreach (var f in data.Folders) { ValidateId(f.Id); SafeFiles.Name(f.Name); }
            data.Jobs = data.Jobs.Select(j => j.State is JobState.Transcribing or JobState.GeneratingNotes ? j with { State = JobState.Queued, Error = "Recovered interrupted processing." } : j).ToList(); Save();
            foreach (var j in data.Jobs.Where(j => j.State == JobState.Purged))
            {
                // A durable tombstone survives interrupted deletion. Unsafe leftovers stay suppressed and can be purged again.
                try { SafeFiles.DeleteTree(Item(j.Id)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch { ownership.Dispose(); throw; }
    }
    public void Dispose() => ownership.Dispose();
    static void ValidateId(string id) { if (!Guid.TryParseExact(id, "D", out var g) || g.ToString() != id) throw new InvalidDataException("Invalid library identity."); }
    void Save() => SafeFiles.Atomic(Path.Combine(root, "library.json"), JsonSerializer.Serialize(data, JsonFormat.Options));
    public string ProcessorId { get { lock (gate) return data.ProcessorId; } }
    public AppSettings Settings { get { lock (gate) return data.Settings; } }
    public IReadOnlyList<RecordingJob> Jobs { get { lock (gate) return data.Jobs.ToArray(); } }
    public IReadOnlyList<LibraryFolder> Folders { get { lock (gate) return data.Folders.ToArray(); } }
    public RecordingJob Get(string id) { lock (gate) return data.Jobs.Single(j => j.Id == id); }
    public void UpdateSettings(AppSettings settings)
    {
        lock (gate)
        {
            if (settings.OutputDirectory != "") SafeFiles.Disjoint(root, settings.OutputDirectory);
            if (settings.MobileDirectory != "") { SafeFiles.Disjoint(root, settings.MobileDirectory); if (settings.OutputDirectory != "") SafeFiles.Disjoint(settings.OutputDirectory, settings.MobileDirectory); }
            if (settings.Models.Compute is not ("CPU" or "CUDA") || settings.Models.TimeoutMinutes is < 1 or > 1440 || settings.Models.GpuLayers is < 0 or > 256) throw new InvalidDataException("Invalid runtime settings.");
            data.Settings = settings; Save();
        }
    }
    public RecordingJob Import(string source, string? title = null, string? recordedAt = null, MobileOrigin? mobile = null)
    {
        lock (gate)
        {
            if (mobile != null && data.Jobs.Any(j => j.Mobile != null && SameMobileRecording(j.Mobile, mobile))) throw new InvalidDataException("Mobile recording identity already exists, including purged recordings.");
            SafeFiles.NoLinks(source); SafeFiles.RecordedAt(recordedAt); title ??= Path.GetFileNameWithoutExtension(source); SafeFiles.Title(title);
            var ext = Path.GetExtension(source).ToLowerInvariant(); if (!new[] { ".wav", ".mp3", ".m4a", ".mp4", ".flac", ".ogg", ".aac", ".wma" }.Contains(ext)) throw new InvalidDataException("Unsupported audio extension.");
            if (new FileInfo(source).Length is <= 0 or > 2147483648) throw new IOException("Audio must be nonempty and at most 2 GiB.");
            var id = Guid.NewGuid().ToString(); var dir = Item(id); Directory.CreateDirectory(dir);
            try
            {
                var dest = Path.Combine(dir, "original" + ext); File.Copy(source, dest, false); var hash = SafeFiles.Hash(dest); if (hash != SafeFiles.Hash(source)) throw new IOException("Source changed while copying.");
                if (mobile != null && (hash != mobile.AudioSha256 || recordedAt != mobile.RecordedAt)) throw new InvalidDataException("Mobile snapshot mismatch.");
                var job = new RecordingJob { Id = id, Title = title, RecordedAt = recordedAt, Extension = ext, AudioSha256 = hash, Models = data.Settings.Models with { }, Mobile = mobile }; data.Jobs.Add(job); try { Save(); } catch { data.Jobs.Remove(job); throw; } return job;
            }
            catch { SafeFiles.DeleteTree(dir); throw; }
        }
    }
    internal static bool SameMobileRecording(MobileOrigin a, MobileOrigin b) => a.RecordingId == b.RecordingId && SafeFiles.Full(a.SyncRoot).Equals(SafeFiles.Full(b.SyncRoot), StringComparison.OrdinalIgnoreCase);
    string Item(string id) { ValidateId(id); var p = Path.Combine(root, "items", id); SafeFiles.NoLinks(p); return p; }
    string ArtifactDirectory(string id, string? revision) { var p = Item(id); if (revision != null) { ValidateId(revision); p = Path.Combine(p, "artifacts", revision); SafeFiles.NoLinks(p); } return p; }
    string ProcessingDirectory(RecordingJob job) => ArtifactDirectory(job.Id, job.PendingArtifactRevision ?? job.ArtifactRevision);
    public string AudioPath(string id) { lock (gate) { var j = Get(id); if (j.Extension.Length > 6 || !j.Extension.StartsWith('.') || j.Extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) throw new InvalidDataException("Invalid audio extension."); var p = Path.Combine(Item(id), "original" + j.Extension); SafeFiles.NoLinks(p); return p; } }
    public string WorkDirectory(string id) { lock (gate) { var p = Path.Combine(ProcessingDirectory(Get(id)), "work"); SafeFiles.NoLinks(p); Directory.CreateDirectory(p); SafeFiles.NoLinks(p); return p; } }
    void Replace(RecordingJob job) { var i = data.Jobs.FindIndex(j => j.Id == job.Id); var previous = data.Jobs[i]; data.Jobs[i] = job; try { Save(); } catch { data.Jobs[i] = previous; throw; } }
    public void SetRecordedAt(string id, string? recordedAt) { lock (gate) { SafeFiles.RecordedAt(recordedAt); var j = Get(id); if (j.Mobile != null && j.RecordedAt != recordedAt) throw new InvalidOperationException("Mobile recording date is immutable."); Replace(j with { RecordedAt = recordedAt, ExportPath = null }); } }
    public void Rename(string id, string title) { lock (gate) { SafeFiles.Title(title); Replace(Get(id) with { Title = title, ExportPath = null }); } }
    public LibraryFolder CreateFolder(string name, string? parentId) { lock (gate) { SafeFiles.Name(name); CheckFolder(parentId); if (data.Folders.Any(f => f.ParentId == parentId && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Folder name exists."); var f = new LibraryFolder(Guid.NewGuid().ToString(), name, parentId); data.Folders.Add(f); Save(); return f; } }
    void CheckFolder(string? id) { if (id != null && !data.Folders.Any(f => f.Id == id)) throw new InvalidDataException("Unknown folder."); }
    public void MoveFolder(string id, string? parentId) { lock (gate) { CheckFolder(id); CheckFolder(parentId); var p = parentId; while (p != null) { if (p == id) throw new InvalidDataException("Folder cycle."); p = data.Folders.Single(f => f.Id == p).ParentId; } var i = data.Folders.FindIndex(f => f.Id == id); var f = data.Folders[i]; if (data.Folders.Any(x => x.Id != id && x.ParentId == parentId && x.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Folder name exists."); data.Folders[i] = f with { ParentId = parentId }; Save(); } }
    public void Move(IEnumerable<string> ids, string? folderId) { lock (gate) { CheckFolder(folderId); foreach (var id in ids.ToArray()) { var j = Get(id); if (j.State == JobState.Purged) continue; Replace(j with { FolderId = folderId, ExportPath = null }); } } }
    public void Trash(IEnumerable<string> ids) { lock (gate) { foreach (var id in ids.ToArray()) { var j = Get(id); if (j.State is JobState.Transcribing or JobState.GeneratingNotes) throw new InvalidOperationException("Cancel processing before trashing."); Replace(j with { Trashed = true }); } } }
    public void Restore(IEnumerable<string> ids) { lock (gate) { foreach (var id in ids.ToArray()) { var j = Get(id); if (j.State != JobState.Purged) Replace(j with { Trashed = false }); } } }
    public void Purge(IEnumerable<string> ids) { lock (gate) { foreach (var id in ids.ToArray()) { var j = Get(id); if (!j.Trashed || j.State is JobState.Transcribing or JobState.GeneratingNotes) throw new InvalidOperationException("Only inactive trash may be purged."); Replace(j with { State = JobState.Purged }); SafeFiles.DeleteTree(Item(id)); } } }
    static bool SameAsr(ModelSelection a, ModelSelection b) => a.WhisperExecutable == b.WhisperExecutable && a.WhisperModel == b.WhisperModel && a.FfmpegExecutable == b.FfmpegExecutable && a.Compute == b.Compute && (a.Compute != "CUDA" || a.CudaConfirmed == b.CudaConfirmed);
    public void Retry(IEnumerable<string> ids, bool useCurrentModels = false)
    {
        lock (gate)
        {
            foreach (var id in ids.ToArray())
            {
                var j = Get(id); if (j.Trashed || j.State is not (JobState.Failed or JobState.Cancelled)) continue;
                var models = useCurrentModels ? data.Settings.Models with { } : j.Models;
                // Notes-only retries need an isolated pair too; seed reusable ASR before selecting the stage durably.
                var reuseAsr = SameAsr(j.Models, models);
                var pending = reuseAsr ? j.PendingArtifactRevision ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                if (reuseAsr && j.PendingArtifactRevision == null)
                {
                    var transcript = ReadTranscriptForProcessing(id);
                    if (!string.IsNullOrWhiteSpace(transcript)) SafeFiles.Atomic(Path.Combine(ArtifactDirectory(id, pending), "transcript.txt"), transcript);
                }
                Replace(j with { State = JobState.Queued, Error = null, Models = models, PendingArtifactRevision = pending, ExportPath = null, ExportError = null });
            }
        }
    }
    public bool TryStart(string id) { lock (gate) { var j = Get(id); if (data.Settings.QueuePaused || j.Trashed || j.State != JobState.Queued) return false; Replace(j with { State = JobState.Transcribing, Error = null }); return true; } }
    public void SaveTranscript(string id, string text) { lock (gate) { if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Transcript must not be empty."); var j = Get(id); SafeFiles.Atomic(Path.Combine(ProcessingDirectory(j), "transcript.txt"), text); Replace(j with { State = JobState.GeneratingNotes }); } }
    public void Complete(string id, string markdown)
    {
        lock (gate)
        {
            var j = Get(id); if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(ReadTranscriptForProcessing(id))) throw new InvalidDataException("Completion requires nonempty transcript and notes.");
            SafeFiles.Atomic(Path.Combine(ProcessingDirectory(j), "notes.md"), markdown);
            Replace(j with { State = JobState.Completed, Error = null, ArtifactRevision = j.PendingArtifactRevision ?? j.ArtifactRevision, PendingArtifactRevision = null, ExportPath = null, ExportError = null });
        }
    }
    public void Fail(string id, string error, bool cancelled = false) { lock (gate) Replace(Get(id) with { State = cancelled ? JobState.Cancelled : JobState.Failed, Error = error }); }
    public void CancelQueued(IEnumerable<string> ids) { lock (gate) { foreach (var id in ids.ToArray()) { var j = Get(id); if (j.State == JobState.Queued) Replace(j with { State = JobState.Cancelled, Error = "Cancelled before processing." }); } } }
    public string ReadTranscript(string id) => ReadArtifact(id, "transcript.txt");
    public string ReadTranscriptForProcessing(string id) { lock (gate) return ReadArtifactAt(ProcessingDirectory(Get(id)), "transcript.txt"); }
    public string ReadNotes(string id) => ReadArtifact(id, "notes.md");
    string ReadArtifact(string id, string file) { lock (gate) return ReadArtifactAt(ArtifactDirectory(id, Get(id).ArtifactRevision), file); }
    static string ReadArtifactAt(string directory, string file) { var p = Path.Combine(directory, file); SafeFiles.NoLinks(p); return File.Exists(p) ? SafeFiles.Read(p) : ""; }
    public void SetExportError(string id, string? error) { lock (gate) { var j = Get(id); if (j.ExportError != error) Replace(j with { ExportError = error }); } }
    public string Export(string id)
    {
        lock (gate)
        {
            var j = Get(id); if (j.State != JobState.Completed || j.Trashed) throw new InvalidOperationException("Export requires a completed recording.");
            if (string.IsNullOrWhiteSpace(data.Settings.OutputDirectory)) throw new IOException("Choose an export directory in Settings.");
            SafeFiles.Disjoint(root, data.Settings.OutputDirectory); var dir = SafeFiles.Full(data.Settings.OutputDirectory);
            var chain = new Stack<string>(); var folder = j.FolderId; var seen = new HashSet<string>(); while (folder != null) { if (!seen.Add(folder)) throw new InvalidDataException("Folder cycle."); var f = data.Folders.Single(f => f.Id == folder); chain.Push(SafeFiles.Name(f.Name)); folder = f.ParentId; }
            foreach (var name in chain) dir = Path.Combine(dir, name);
            SafeFiles.NoLinks(dir); Directory.CreateDirectory(dir);
            if (j.ExportPath != null && Path.GetDirectoryName(j.ExportPath) == dir && File.Exists(j.ExportPath)) { SafeFiles.NoLinks(j.ExportPath); return j.ExportPath; }
            var stem = SafeFiles.FileTitle(j.Title) + " - " + (j.RecordedAt?[..10] ?? "date-unknown"); var temp = Path.Combine(dir, ".tmp-" + Guid.NewGuid() + ".zip");
            try
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    void Add(string path, string name) { SafeFiles.NoLinks(path); zip.CreateEntryFromFile(path, name); }
                    var artifacts = ArtifactDirectory(id, j.ArtifactRevision);
                    Add(AudioPath(id), "original" + j.Extension); Add(Path.Combine(artifacts, "transcript.txt"), "transcript.txt"); Add(Path.Combine(artifacts, "notes.md"), "notes.md");
                }
                for (int n = 1; n < 10000; n++) { var dest = Path.Combine(dir, stem + (n == 1 ? "" : $" ({n})") + ".zip"); SafeFiles.NoLinks(dest); try { File.Move(temp, dest, false); } catch (IOException) when (File.Exists(dest)) { continue; } Replace(j with { ExportPath = dest, ExportError = null }); return dest; }
                throw new IOException("Too many export filename collisions.");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
