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
            foreach (var j in data.Jobs) { ValidateId(j.Id); SafeFiles.Title(j.Title); SafeFiles.RecordedAt(j.RecordedAt); RecordingTitles.ValidateDay(j.RecordingDate); if (j.Mobile != null && j.RecordingDate != null && j.RecordingDate != j.RecordedAt?[..10]) throw new InvalidDataException("Mobile recording date is immutable."); if (j.ArtifactRevision != null) ValidateId(j.ArtifactRevision); if (j.PendingArtifactRevision != null) { ValidateId(j.PendingArtifactRevision); if (j.PendingArtifactRevision == j.ArtifactRevision) throw new InvalidDataException("Pending artifacts must not overwrite the committed revision."); } }
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
            if (settings.InputDirectory != "")
            {
                SafeFiles.Disjoint(root, settings.InputDirectory);
                if (settings.OutputDirectory != "") SafeFiles.Disjoint(settings.InputDirectory, settings.OutputDirectory);
                if (settings.MobileDirectory != "") SafeFiles.Disjoint(settings.InputDirectory, settings.MobileDirectory);
            }
            if (settings.Models.ComputePreference is not ("Auto" or "CPU" or "CUDA")) throw new InvalidDataException("Invalid compute preference.");
            if (settings.Models.Compute is not ("CPU" or "CUDA") || settings.Models.TimeoutMinutes is < 1 or > 1440 || settings.Models.GpuLayers is < 0 or > 256) throw new InvalidDataException("Invalid runtime settings.");
            data.Settings = settings; Save();
        }
    }
    // Compare complete model snapshots so a slow preparation cannot overwrite edited settings.
    public bool TryApplyPreparedSettingsModels(ModelSelection expected, ModelSelection prepared)
    {
        lock (gate)
        {
            if (data.Settings.Models != expected) return false;
            var previous = data.Settings;
            data.Settings = previous with { Models = prepared };
            try { Save(); } catch { data.Settings = previous; throw; }
            return true;
        }
    }
    public bool TryApplyPreparedJobModels(string id, ModelSelection expected, ModelSelection prepared)
    {
        lock (gate)
        {
            var job = Get(id);
            if (job.State != JobState.Queued || job.Trashed || job.Models != expected) return false;
            Replace(job with { Models = prepared });
            return true;
        }
    }
    public RecordingJob Import(string source, string? title = null, string? recordedAt = null, MobileOrigin? mobile = null, string? folderId = null)
    {
        lock (gate)
        {
            CheckFolder(folderId);
            if (mobile != null && data.Jobs.Any(j => j.Mobile != null && SameMobileRecording(j.Mobile, mobile))) throw new InvalidDataException("Mobile recording identity already exists, including purged recordings.");
            SafeFiles.NoLinks(source); SafeFiles.RecordedAt(recordedAt); title ??= Path.GetFileNameWithoutExtension(source); SafeFiles.Title(title);
            var ext = Path.GetExtension(source).ToLowerInvariant(); if (!new[] { ".wav", ".mp3", ".m4a", ".mp4", ".flac", ".ogg", ".aac", ".wma" }.Contains(ext)) throw new InvalidDataException("Unsupported audio extension.");
            if (new FileInfo(source).Length is <= 0 or > 2147483648) throw new IOException("Audio must be nonempty and at most 2 GiB.");
            var id = Guid.NewGuid().ToString(); var dir = Item(id); Directory.CreateDirectory(dir);
            try
            {
                var dest = Path.Combine(dir, "original" + ext); File.Copy(source, dest, false); var hash = SafeFiles.Hash(dest); if (hash != SafeFiles.Hash(source)) throw new IOException("Source changed while copying.");
                if (mobile != null && (hash != mobile.AudioSha256 || recordedAt != mobile.RecordedAt)) throw new InvalidDataException("Mobile snapshot mismatch.");
                var job = new RecordingJob { Id = id, Title = title, TitleOwnership = TitleOwnership.Imported, RecordedAt = recordedAt, Extension = ext, AudioSha256 = hash, Models = data.Settings.Models with { }, Mobile = mobile, FolderId = folderId }; data.Jobs.Add(job); try { Save(); } catch { data.Jobs.Remove(job); throw; } return job;
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
    public void SetRecordedAt(string id, string? recordedAt)
    {
        lock (gate)
        {
            SafeFiles.RecordedAt(recordedAt); var j = Get(id);
            if (j.Mobile != null && j.RecordedAt != recordedAt) throw new InvalidOperationException("Mobile recording date is immutable.");
            Replace(j with { Title = RecordingTitles.BaseTitle(j.Title), RecordedAt = recordedAt, RecordingDate = null, ExportPath = null });
        }
    }
    public void Rename(string id, string title)
    { lock (gate) { var j = Get(id); Replace(j with { Title = RecordingTitles.BaseTitle(title), TitleOwnership = TitleOwnership.Manual, TitleRevision = checked(j.TitleRevision + 1), ExportPath = null }); } }
    public void UpdateTitleAndDate(string id, string title, string? recordingDate, bool updateTitle = true)
    {
        lock (gate)
        {
            var j = Get(id); var baseTitle = updateTitle ? RecordingTitles.BaseTitle(title) : j.Title; RecordingTitles.ValidateDay(recordingDate);
            if (j.Mobile != null && recordingDate != null && recordingDate != j.RecordedAt?[..10]) throw new InvalidOperationException("Mobile recording date is immutable.");
            Replace(j with { Title = baseTitle, TitleOwnership = updateTitle ? TitleOwnership.Manual : j.TitleOwnership, TitleRevision = updateTitle ? checked(j.TitleRevision + 1) : j.TitleRevision, RecordingDate = recordingDate, ExportPath = null });
        }
    }
    static bool SameTitleSnapshot(RecordingJob current, RecordingJob expected) => current.TitleRevision == expected.TitleRevision && current.TitleOwnership == expected.TitleOwnership && current.Title == expected.Title;
    static RecordingJob AutomaticTitle(RecordingJob current, RecordingJob expected, NotesDocument notes)
    {
        if (current.TitleOwnership is not (TitleOwnership.Imported or TitleOwnership.Automatic) || !SameTitleSnapshot(current, expected)) return current;
        var title = RecordingTitles.FromNotes(notes);
        return title == null ? current : current with { Title = title, TitleOwnership = TitleOwnership.Automatic, TitleRevision = checked(current.TitleRevision + 1), ExportPath = null };
    }
    public bool TryApplyAutomaticTitle(RecordingJob expected, NotesDocument notes)
    {
        lock (gate)
        {
            var current = Get(expected.Id); var updated = AutomaticTitle(current, expected, notes);
            if (ReferenceEquals(updated, current)) return false;
            Replace(updated); return true;
        }
    }
    public bool RegenerateTitle(string id)
    {
        lock (gate)
        {
            var job = Get(id);
            // Only the committed artifact's structured notes are eligible, never a retry's work or arbitrary Markdown.
            var path = Path.Combine(ArtifactDirectory(id, job.ArtifactRevision), "work", "notes.json");
            SafeFiles.NoLinks(path);
            if (job.State == JobState.Purged || string.IsNullOrWhiteSpace(ReadNotes(id)) || !File.Exists(path))
                throw new InvalidOperationException("저장된 구조화 노트가 없습니다. 노트 생성을 완료한 뒤 다시 시도하세요.");
            var notes = NotesDocument.Parse(SafeFiles.Read(path), JobRunner.SourceLines(ReadTranscript(id)));
            var title = RecordingTitles.FromNotes(notes);
            if (title == null) return false;
            Replace(job with { Title = title, TitleOwnership = TitleOwnership.Automatic, TitleRevision = checked(job.TitleRevision + 1), ExportPath = null });
            return true;
        }
    }
    public LibraryFolder CreateFolder(string name, string? parentId)
    {
        lock (gate)
        {
            SafeFiles.Name(name); CheckFolder(parentId);
            if (data.Folders.Any(f => f.ParentId == parentId && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Folder name exists.");
            var folder = new LibraryFolder(Guid.NewGuid().ToString(), name, parentId);
            SaveFolders([.. data.Folders, folder], data.Jobs); return folder;
        }
    }
    void CheckFolder(string? id) { if (id != null && !data.Folders.Any(f => f.Id == id)) throw new InvalidDataException("Unknown folder."); }
    LibraryFolder GetFolder(string id) => data.Folders.SingleOrDefault(f => f.Id == id) ?? throw new InvalidDataException("Unknown folder.");
    HashSet<string> FolderTree(string id)
    {
        var children = data.Folders.Where(f => f.ParentId != null).ToLookup(f => f.ParentId!);
        var ids = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(id);
        while (pending.TryPop(out var current))
        {
            if (!ids.Add(current)) continue;
            foreach (var child in children[current]) pending.Push(child.Id);
        }
        return ids;
    }
    void SaveFolders(List<LibraryFolder> folders, List<RecordingJob> jobs)
    {
        // Persist filing and export invalidation together; keep both snapshots on failure.
        var previousFolders = data.Folders; var previousJobs = data.Jobs;
        data.Folders = folders; data.Jobs = jobs;
        try { Save(); } catch { data.Folders = previousFolders; data.Jobs = previousJobs; throw; }
    }
    public void RenameFolder(string id, string name)
    {
        lock (gate)
        {
            var folder = GetFolder(id); SafeFiles.Name(name);
            if (data.Folders.Any(f => f.Id != id && f.ParentId == folder.ParentId && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Folder name exists.");
            if (folder.Name == name) return;
            var affected = FolderTree(id);
            SaveFolders(data.Folders.Select(f => f.Id == id ? f with { Name = name } : f).ToList(),
                data.Jobs.Select(j => j.FolderId != null && affected.Contains(j.FolderId) ? j with { ExportPath = null } : j).ToList());
        }
    }
    public void DeleteFolder(string id)
    {
        lock (gate)
        {
            GetFolder(id); var affected = FolderTree(id);
            // Folder deletion changes filing only, including trash and purge tombstones.
            SaveFolders(data.Folders.Where(f => !affected.Contains(f.Id)).ToList(),
                data.Jobs.Select(j => j.FolderId != null && affected.Contains(j.FolderId) ? j with { FolderId = null, ExportPath = null } : j).ToList());
        }
    }
    public void MoveFolder(string id, string? parentId)
    {
        lock (gate)
        {
            var folder = GetFolder(id); CheckFolder(parentId); var affected = FolderTree(id);
            if (parentId != null && affected.Contains(parentId)) throw new InvalidDataException("Folder cycle.");
            if (data.Folders.Any(f => f.Id != id && f.ParentId == parentId && f.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Folder name exists.");
            if (folder.ParentId == parentId) return;
            SaveFolders(data.Folders.Select(f => f.Id == id ? f with { ParentId = parentId } : f).ToList(),
                data.Jobs.Select(j => j.FolderId != null && affected.Contains(j.FolderId) ? j with { ExportPath = null } : j).ToList());
        }
    }
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
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        var timings = TranscriptTimingFile.Read(Path.Combine(ProcessingDirectory(j), TranscriptTimingFile.FileName), transcript);
                        var target = ArtifactDirectory(id, pending);
                        SafeFiles.Atomic(Path.Combine(target, "transcript.txt"), transcript);
                        if (timings.Count > 0) SafeFiles.Atomic(Path.Combine(target, TranscriptTimingFile.FileName), TranscriptTimingFile.Serialize(transcript, timings));
                    }
                }
                Replace(j with { State = JobState.Queued, Error = null, Models = models, PendingArtifactRevision = pending, ExportPath = null, ExportError = null });
            }
        }
    }
    public void Retranscribe(IEnumerable<string> ids)
    {
        lock (gate)
        {
            foreach (var id in ids.ToArray())
            {
                var j = Get(id); if (j.Trashed || j.State is not (JobState.Completed or JobState.Failed or JobState.Cancelled)) continue;
                // Never seed or resume cached ASR, including a previous unfinished replacement.
                Replace(j with { State = JobState.Queued, Error = null, PendingArtifactRevision = Guid.NewGuid().ToString() });
            }
        }
    }
    public bool TryStart(string id) { lock (gate) { var j = Get(id); if (data.Settings.QueuePaused || j.Trashed || j.State != JobState.Queued) return false; Replace(j with { State = JobState.Transcribing, Error = null }); return true; } }
    public void SaveTranscript(string id, string text, IReadOnlyList<TranscriptSegment>? segments = null)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Transcript must not be empty.");
            var sidecar = segments is { Count: > 0 } ? TranscriptTimingFile.Serialize(text, segments) : null;
            var j = Get(id); var directory = ProcessingDirectory(j);
            // Text hash in the sidecar prevents association across an interrupted two-file write.
            SafeFiles.Atomic(Path.Combine(directory, "transcript.txt"), text);
            var path = Path.Combine(directory, TranscriptTimingFile.FileName); SafeFiles.NoLinks(path);
            if (sidecar != null) SafeFiles.Atomic(path, sidecar); else File.Delete(path);
            Replace(j with { State = JobState.GeneratingNotes });
        }
    }
    public void Complete(string id, string markdown)
    {
        lock (gate)
        {
            var j = Get(id); if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(ReadTranscriptForProcessing(id))) throw new InvalidDataException("Completion requires nonempty transcript and notes.");
            CompleteCore(j, markdown);
        }
    }
    // The runner supplies parsed, source-validated notes and its pre-generation title snapshot.
    public void Complete(string id, NotesDocument notes, RecordingJob expected)
    {
        lock (gate)
        {
            var job = Get(id);
            if (expected.Id != id || string.IsNullOrWhiteSpace(ReadTranscriptForProcessing(id))) throw new InvalidDataException("Completion requires matching transcript and notes.");
            var updated = AutomaticTitle(job, expected, notes);
            CompleteCore(updated, notes.Markdown(RecordingTitles.Display(updated)));
        }
    }
    void CompleteCore(RecordingJob job, string markdown)
    {
        SafeFiles.Atomic(Path.Combine(ProcessingDirectory(job), "notes.md"), markdown);
        Replace(job with { State = JobState.Completed, Error = null, ArtifactRevision = job.PendingArtifactRevision ?? job.ArtifactRevision, PendingArtifactRevision = null, ExportPath = null, ExportError = null });
    }
    public void Fail(string id, string error, bool cancelled = false) { lock (gate) Replace(Get(id) with { State = cancelled ? JobState.Cancelled : JobState.Failed, Error = error }); }
    public void CancelQueued(IEnumerable<string> ids) { lock (gate) { foreach (var id in ids.ToArray()) { var j = Get(id); if (j.State == JobState.Queued) Replace(j with { State = JobState.Cancelled, Error = "Cancelled before processing." }); } } }
    public string ReadTranscript(string id) => ReadArtifact(id, "transcript.txt");
    public TranscriptDocument ReadTranscriptDocument(string id)
    {
        lock (gate)
        {
            var j = Get(id); var directory = ArtifactDirectory(id, j.ArtifactRevision); var text = ReadArtifactAt(directory, "transcript.txt");
            return new(text, TranscriptTimingFile.Read(Path.Combine(directory, TranscriptTimingFile.FileName), text));
        }
    }
    internal IReadOnlyList<TranscriptSegment> ReadProcessingTimings(string id, string text)
    { lock (gate) return TranscriptTimingFile.Read(Path.Combine(ProcessingDirectory(Get(id)), TranscriptTimingFile.FileName), text); }
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
            var stem = SafeFiles.FileTitle(RecordingTitles.BaseTitle(j.Title)) + " - " + (RecordingTitles.Day(j) ?? "date-unknown"); var temp = Path.Combine(dir, ".tmp-" + Guid.NewGuid() + ".zip");
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
