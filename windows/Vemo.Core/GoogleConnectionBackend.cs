using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Vemo.Core;

public sealed partial class GoogleConnectionService
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object sessionLock = new();
    CancellationTokenSource session = new();
    GoogleOAuthClient? oauth;
    GoogleDriveClient? drive;
    bool disposed;
    Journal journal = new();
    string StateDirectory => Path.Combine(store.Root, "google");
    string JournalPath => Path.Combine(StateDirectory, "binding.json");
    string Cache => Path.Combine(StateDirectory, "cache", GoogleDriveClient.PathKey(journal.AccountId + "/" + journal.FolderId + "/" + store.ProcessorId));
    sealed class Journal
    {
        public string? AccountId { get; set; }
        public string? FolderId { get; set; }
        public string? FolderName { get; set; }
        public string? ProcessorId { get; set; }
        public string? RootIntentId { get; set; }
        public string? RootIntentName { get; set; }
        public Dictionary<string, string> Ids { get; set; } = [];
    }
    void SaveJournal() => SafeFiles.Atomic(JournalPath, JsonSerializer.Serialize(journal));
    void Hydrate()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                using var doc = JsonFormat.Strict(SafeFiles.Read(JournalPath)); journal = doc.Deserialize<Journal>() ?? throw new GoogleConnectionException("invalid_binding");
                if (journal.AccountId != null) GoogleDriveClient.Identifier(journal.AccountId);
                if (journal.FolderId != null) GoogleDriveClient.Identifier(journal.FolderId);
                if (journal.ProcessorId != null && journal.ProcessorId != store.ProcessorId) throw new GoogleConnectionException("processor_mismatch");
            }
            var config = vault.Load("configuration");
            if (config != null) SetConfiguration(GoogleOAuthConfiguration.ParseInstalledJson(config));
            else
            {
                var bundled = bundledClientJson();
                if (bundled != null) SetConfiguration(GoogleOAuthConfiguration.ParseInstalledJson(GoogleBundledClientConfiguration.NormalizeInstalledJson(bundled)));
            }
            UpdateStatus();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Status = Status with { LastError = e is GoogleConnectionException g ? g.Code : "local_state_unavailable" }; }
    }
    void SetConfiguration(GoogleOAuthConfiguration config)
    { oauth?.Dispose(); oauth = new(config, vault, transport); drive = new(oauth, transport); }
    void UpdateStatus()
    { Status = Status with { Configured = oauth != null, Connected = oauth?.Connected == true, FolderId = journal.FolderId, FolderName = journal.FolderName }; }
    void Notify() { try { Changed(); } catch (Exception) { /* UI observers cannot change operation outcome. */ } }
    async Task<T> Run<T>(Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        CancellationTokenSource operation;
        lock (sessionLock) { ObjectDisposedException.ThrowIf(disposed, this); operation = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token); }
        using (operation)
        {
            operation.CancelAfter(TimeSpan.FromMinutes(30));
            await gate.WaitAsync(operation.Token);
            try
            {
                operation.Token.ThrowIfCancellationRequested(); Status = Status with { Busy = true, LastError = null }; Notify();
                var result = await body(operation.Token); operation.Token.ThrowIfCancellationRequested(); return result;
            }
            catch (Exception e) { Status = Status with { LastError = e is OperationCanceledException ? "cancelled" : e is GoogleConnectionException g ? g.Code : "operation_failed" }; throw; }
            finally
            {
                try { UpdateStatus(); } catch { Status = Status with { Connected = false }; }
                Status = Status with { Busy = false }; gate.Release(); Notify();
            }
        }
    }
    GoogleDriveClient Drive => drive ?? throw new GoogleConnectionException("configuration_required");
    private partial async Task ImportCoreAsync(string json, CancellationToken ct)
    {
        var config = GoogleOAuthConfiguration.ParseInstalledJson(json);
        await StopAsync();
        await Run(async token => { token.ThrowIfCancellationRequested(); oauth?.Disconnect(); vault.Delete("refresh"); vault.Save("configuration", config.Serialize()); SetConfiguration(config); await Task.CompletedTask; return true; }, ct);
    }
    public Task ConnectAsync(Func<Uri, Task> openBrowser, CancellationToken ct = default) => Run(async token =>
    { if (oauth == null) throw new GoogleConnectionException("configuration_required"); await oauth.ConnectAsync(openBrowser, token); return true; }, ct);
    private partial Task ConnectCoreAsync(CancellationToken ct) => ConnectAsync(uri => { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }, ct);
    public async Task StopAsync()
    {
        CancellationTokenSource previous;
        lock (sessionLock) { if (disposed) return; previous = session; session = new(); }
        previous.Cancel();
        await gate.WaitAsync(); gate.Release(); previous.Dispose();
    }
    private partial async Task DisconnectCoreAsync(CancellationToken ct)
    {
        await StopAsync();
        await Run(token => { token.ThrowIfCancellationRequested(); oauth?.Disconnect(); vault.Delete("refresh"); return Task.FromResult(true); }, ct);
    }
    private partial Task<IReadOnlyList<GoogleDriveFolder>> ListCoreAsync(CancellationToken ct) => Run<IReadOnlyList<GoogleDriveFolder>>(async token => (await Drive.RootsAsync(token)).Where(x => x.ProcessorId == store.ProcessorId).ToArray(), ct);
    async Task Checked(CancellationToken ct)
    {
        if (journal.FolderId == null || journal.AccountId == null || journal.ProcessorId != store.ProcessorId) throw new GoogleConnectionException("folder_required");
        if (await Drive.AccountIdAsync(ct) != journal.AccountId) throw new GoogleConnectionException("account_mismatch");
        var folder = GoogleDriveClient.Root(await Drive.FileAsync(journal.FolderId, ct)); if (folder.ProcessorId != store.ProcessorId) throw new GoogleConnectionException("processor_mismatch");
    }
    async Task Bind(GoogleDriveFolder folder, string account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (folder.ProcessorId != store.ProcessorId) throw new GoogleConnectionException("processor_mismatch");
        if (journal.AccountId != null && journal.AccountId != account || journal.FolderId != null && journal.FolderId != folder.Id || journal.RootIntentId != null && journal.RootIntentId != folder.Id) throw new GoogleConnectionException("binding_conflict");
        journal.AccountId = account; journal.FolderId = folder.Id; journal.FolderName = folder.Name; journal.ProcessorId = store.ProcessorId; SaveJournal();
        await Prepare(ct);
    }
    private partial Task SelectCoreAsync(string id, CancellationToken ct) => Run(async token =>
    {
        GoogleDriveClient.Identifier(id); var roots = await Drive.RootsAsync(token); var root = roots.SingleOrDefault(x => x.Id == id) ?? throw new GoogleConnectionException("folder_not_accessible");
        await Bind(root, await Drive.AccountIdAsync(token), token); return true;
    }, ct);
    private partial Task<GoogleDriveFolder> CreateCoreAsync(string name, CancellationToken ct) => Run(async token =>
    {
        var defaultName = string.IsNullOrWhiteSpace(name) || name.Trim() == "Vemo Windows";
        if (defaultName) name = "Vemo Windows";
        SafeFiles.Title(name); var account = await Drive.AccountIdAsync(token);
        if (journal.FolderId != null) { await Checked(token); return GoogleDriveClient.Root(await Drive.FileAsync(journal.FolderId, token)); }
        if (journal.AccountId != null && journal.AccountId != account) throw new GoogleConnectionException("account_mismatch");
        var roots = await Drive.RootsAsync(token);
        if (defaultName)
        {
            if (journal.RootIntentId != null) name = journal.RootIntentName ?? throw new GoogleConnectionException("binding_conflict");
            else
            {
                var names = roots.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                for (var n = 2; names.Contains(name); n++) name = "Vemo Windows (" + n + ")";
            }
        }
        if (journal.RootIntentId == null)
        {
            if (roots.Any(x => x.Name == name || x.ProcessorId == store.ProcessorId)) throw new GoogleConnectionException("duplicate_roots");
            journal.AccountId = account; journal.ProcessorId = store.ProcessorId; journal.RootIntentName = name; journal.RootIntentId = await Drive.AllocateIdAsync(token); token.ThrowIfCancellationRequested(); SaveJournal();
        }
        if (journal.RootIntentName != name) throw new GoogleConnectionException("binding_conflict");
        GoogleDriveFile file;
        try { file = await Drive.FileAsync(journal.RootIntentId, token); }
        catch (GoogleConnectionException e) when (e.Code == "not_found") { file = await Drive.CreateAsync(journal.RootIntentId, name, null, GoogleDriveClient.FolderMime, new() { ["vemoProtocol"] = "1", ["vemoKind"] = "root", ["vemoProcessor"] = store.ProcessorId }, token); }
        var folder = GoogleDriveClient.Root(file); if (folder.Name != name) throw new GoogleConnectionException("binding_conflict"); await Bind(folder, account, token); return folder;
    }, ct);
    async Task<GoogleDriveFile> Ensure(string parent, string path, string mime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var file = await Drive.ChildAsync(parent, path, mime, ct);
        if (file != null)
        { if (journal.Ids.TryGetValue(path, out var reserved) && reserved != file.Id) throw new GoogleConnectionException("binding_conflict"); return file; }
        if (!journal.Ids.TryGetValue(path, out var id)) { id = await Drive.AllocateIdAsync(ct); ct.ThrowIfCancellationRequested(); journal.Ids.Add(path, id); SaveJournal(); }
        try { file = await Drive.FileAsync(id, ct); }
        catch (GoogleConnectionException e) when (e.Code == "not_found") { file = await Drive.CreateAsync(id, path.Split('/')[^1], parent, mime, GoogleDriveClient.PropertiesFor(path), ct); }
        GoogleDriveClient.Validate(file, parent, path, mime); return file;
    }
    async Task<GoogleDriveFile> EnsureJson(string parent, string path, byte[] bytes, CancellationToken ct)
    {
        var existing = await Drive.ChildAsync(parent, path, "application/json", ct);
        if (existing != null)
        {
            if (journal.Ids.TryGetValue(path, out var reserved) && reserved != existing.Id) throw new GoogleConnectionException("binding_conflict");
            return existing;
        }
        if (!journal.Ids.TryGetValue(path, out var id))
        { id = await Drive.AllocateIdAsync(ct); ct.ThrowIfCancellationRequested(); journal.Ids.Add(path, id); SaveJournal(); }
        // Resolve lost acknowledgments by reserved ID before another create attempt.
        try
        {
            var file = await Drive.FileAsync(id, ct); GoogleDriveClient.Validate(file, parent, path, "application/json"); return file;
        }
        catch (GoogleConnectionException e) when (e.Code == "not_found") { }
        ct.ThrowIfCancellationRequested();
        var created = await Drive.CreateJsonAsync(id, parent, path, bytes, ct);
        var listed = await Drive.ChildAsync(parent, path, "application/json", ct);
        if (listed != null && listed.Id != id) throw new GoogleConnectionException("binding_conflict");
        return created;
    }
    string Local(string path)
    {
        var components = path.Split('/'); if (components.Any(x => x is "" or "." or ".." || x.Contains('\\') || x.Contains(':'))) throw new GoogleConnectionException("invalid_path");
        var result = Path.Combine(Cache, Path.Combine(components)); SafeFiles.NoLinks(result); return result;
    }
    void LocalBytes(string path, byte[] bytes)
    {
        var target = Local(path);
        if (File.Exists(target)) { if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) throw new GoogleConnectionException("cache_conflict"); return; }
        SafeFiles.Atomic(target, new UTF8Encoding(false, true).GetString(bytes), false);
    }
    async Task Prepare(CancellationToken ct)
    {
        await Checked(ct);
        var initialBytes = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, processor_id = store.ProcessorId, display_name = "Vemo Windows" });
        var ensured = await EnsureJson(journal.FolderId!, "processor.json", initialBytes, ct);
        var file = await Drive.FileAsync(ensured.Id, ct);
        GoogleDriveClient.Validate(file, journal.FolderId!, "processor.json", "application/json");
        byte[] bytes;
        if (file.Size is > 0) bytes = await Drive.JsonAsync(file, ct);
        else
        {
            if (journal.Ids.GetValueOrDefault("processor.json") != file.Id) throw new GoogleConnectionException("foreign_empty_file");
            if (file.ETag == null) throw new GoogleConnectionException("recovery_required");
            bytes = initialBytes;
            await Drive.UploadAsync(file.Id, bytes, file.ETag ?? "", ct);
            var confirmed = await Drive.FileAsync(file.Id, ct); GoogleDriveClient.Validate(confirmed, journal.FolderId!, "processor.json", "application/json");
            if (!(await Drive.JsonAsync(confirmed, ct)).AsSpan().SequenceEqual(bytes)) throw new GoogleConnectionException("upload_mismatch");
        }
        ValidateOwner(bytes); LocalBytes("processor.json", bytes);
    }
    void ValidateOwner(byte[] bytes)
    {
        using var doc = GoogleWire.Strict(bytes); var p = doc.RootElement; GoogleWire.Keys(p, "schema_version", "processor_id", "display_name");
        if (p.GetProperty("schema_version").GetInt32() != 1 || p.GetProperty("processor_id").GetString() != store.ProcessorId) throw new GoogleConnectionException("processor_mismatch"); SafeFiles.Title(p.GetProperty("display_name").GetString()!);
    }
    static void Uuid(string id) { if (!Guid.TryParseExact(id, "D", out var g) || g.ToString() != id) throw new GoogleConnectionException("invalid_recording_id"); }
    static void Hash(string hash) { if (hash.Length != 64 || hash.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9'))) throw new GoogleConnectionException("invalid_hash"); }
    sealed record Request(MobileOrigin Origin, long Bytes, bool Eligible);
    Request Decode(byte[] bytes, string recording, string hash)
    {
        using var doc = GoogleWire.Strict(bytes); var p = doc.RootElement;
        GoogleWire.Keys(p, "schema_version", "recording_id", "audio_sha256", "audio_bytes", "audio_file", "recorded_at", "title", "mode", "target_processor_id", "completed");
        Uuid(recording); Hash(hash);
        var date = p.GetProperty("recorded_at").GetString()!; SafeFiles.RecordedAt(date); if (date == null) throw new GoogleConnectionException("invalid_date");
        var title = p.GetProperty("title").GetString()!; SafeFiles.Title(title); var size = p.GetProperty("audio_bytes").GetInt64();
        var mode = p.GetProperty("mode").GetString(); var target = p.GetProperty("target_processor_id"); if (target.ValueKind != JsonValueKind.Null) Uuid(target.GetString()!);
        if (p.GetProperty("schema_version").GetInt32() != 1 || p.GetProperty("recording_id").GetString() != recording || p.GetProperty("audio_sha256").GetString() != hash || p.GetProperty("audio_file").GetString() != "audio.m4a" || size is <= 0 or > 2147483648 || mode is not ("desktop" or "record_only")) throw new GoogleConnectionException("invalid_request");
        return new(new(Cache, recording, hash, store.ProcessorId, date, title), size, p.GetProperty("completed").GetBoolean() && mode == "desktop" && target.ValueKind == JsonValueKind.String && target.GetString() == store.ProcessorId);
    }
    async Task<IReadOnlyList<GoogleDriveFile>> Folders(string parent, string prefix, bool hash, CancellationToken ct)
    {
        var files = await Drive.ChildrenAsync(parent, ct); var names = new HashSet<string>();
        foreach (var file in files) { if (hash) Hash(file.Name); else Uuid(file.Name); if (!names.Add(file.Name)) throw new GoogleConnectionException("duplicate_path"); GoogleDriveClient.Validate(file, parent, prefix + "/" + file.Name, GoogleDriveClient.FolderMime); }
        return files;
    }
    private partial Task<GoogleSyncResult> SyncCoreAsync(CancellationToken ct) => Run(async token =>
    {
        await Checked(token); await Prepare(token); int imported = 0, published = 0, skipped = 0, count = 0;
        var recordings = await Drive.ChildAsync(journal.FolderId!, "recordings", GoogleDriveClient.FolderMime, token);
        if (recordings != null)
        {
            foreach (var recording in await Folders(recordings.Id, "recordings", false, token))
            foreach (var revision in await Folders(recording.Id, "recordings/" + recording.Name, true, token))
            {
                token.ThrowIfCancellationRequested(); if (++count > 50000) throw new GoogleConnectionException("recording_limit");
                var path = "recordings/" + recording.Name + "/" + revision.Name;
                var marker = await Drive.ChildAsync(revision.Id, path + "/recording.json", "application/json", token); if (marker == null) { skipped++; continue; }
                var bytes = await Drive.JsonAsync(marker, token); var request = Decode(bytes, recording.Name, revision.Name); if (!request.Eligible) { skipped++; continue; }
                var existing = store.Jobs.SingleOrDefault(x => x.Mobile != null && LibraryStore.SameMobileRecording(x.Mobile, request.Origin));
                if (existing != null) { if (existing.Mobile != request.Origin) throw new GoogleConnectionException("recording_changed"); skipped++; continue; }
                var audio = await Drive.ChildAsync(revision.Id, path + "/audio.m4a", "audio/mp4", token) ?? throw new GoogleConnectionException("audio_missing");
                if (audio.Size != request.Bytes) throw new GoogleConnectionException("audio_size_mismatch");
                var dest = Local(path + "/audio.m4a"); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); SafeFiles.NoLinks(dest); var temp = dest + ".tmp-" + Guid.NewGuid();
                try
                {
                    long received = 0; using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await Drive.DownloadAsync(audio.Id, request.Bytes, async (block, t) => { received += block.Length; digest.AppendData(block.Span); await output.WriteAsync(block, t); }, token); output.Flush(true);
                    }
                    if (received != request.Bytes || Convert.ToHexStringLower(digest.GetHashAndReset()) != request.Origin.AudioSha256) throw new GoogleConnectionException("audio_hash_mismatch");
                    if (!(await Drive.JsonAsync(marker, token)).AsSpan().SequenceEqual(bytes)) throw new GoogleConnectionException("recording_changed");
                    await Checked(token); token.ThrowIfCancellationRequested();
                    if (File.Exists(dest)) { if (SafeFiles.Hash(dest) != request.Origin.AudioSha256) throw new GoogleConnectionException("cache_conflict"); } else File.Move(temp, dest, false);
                    LocalBytes(path + "/recording.json", bytes); token.ThrowIfCancellationRequested(); store.Import(dest, request.Origin.OriginalTitle, request.Origin.RecordedAt, request.Origin); imported++;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        ExceptionDispatchInfo? publicationFailure = null;
        foreach (var job in store.Jobs.Where(x => x.State == JobState.Completed && !x.Trashed && x.Mobile?.SyncRoot == Cache))
        {
            token.ThrowIfCancellationRequested();
            try { await Publish(job, token); published++; }
            // Only known recording-specific failures may be deferred. Authentication, binding,
            // root, transport, and unknown failures still abort the operation immediately.
            catch (GoogleConnectionException e) when (e.Code is
                "conditional_update_unavailable" or "remote_conflict" or "result_conflict" or
                "recording_changed" or "request_missing" or "audio_missing" or
                "audio_size_mismatch" or "audio_hash_mismatch" or "artifacts_missing" or
                "foreign_empty_file" or "recovery_required" or "upload_mismatch" or "stale_job")
            {
                token.ThrowIfCancellationRequested();
                publicationFailure ??= ExceptionDispatchInfo.Capture(e);
            }
        }
        // Preserve the exception/LastError contract, and never report a partial batch as success.
        token.ThrowIfCancellationRequested(); publicationFailure?.Throw();
        token.ThrowIfCancellationRequested(); Status = Status with { LastSync = DateTimeOffset.UtcNow }; return new GoogleSyncResult(imported, published, skipped);
    }, ct);
    void Live(RecordingJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (store.Get(job.Id) != job) throw new GoogleConnectionException("stale_job");
    }
    async Task Publish(RecordingJob job, CancellationToken ct)
    {
        Live(job, ct); var mobile = job.Mobile!; if (mobile.ProcessorId != store.ProcessorId || mobile.RecordedAt != job.RecordedAt || SafeFiles.Hash(store.AudioPath(job.Id)) != mobile.AudioSha256) throw new GoogleConnectionException("recording_changed");
        var path = "recordings/" + mobile.RecordingId + "/" + mobile.AudioSha256; var localBytes = File.ReadAllBytes(Local(path + "/recording.json")); var request = Decode(localBytes, mobile.RecordingId, mobile.AudioSha256);
        if (!request.Eligible || request.Origin != mobile) throw new GoogleConnectionException("recording_changed");
        string parent = journal.FolderId!, current = "";
        foreach (var part in path.Split('/')) { current = current == "" ? part : current + "/" + part; parent = (await Drive.ChildAsync(parent, current, GoogleDriveClient.FolderMime, ct) ?? throw new GoogleConnectionException("request_missing")).Id; }
        var marker = await Drive.ChildAsync(parent, path + "/recording.json", "application/json", ct) ?? throw new GoogleConnectionException("request_missing");
        if (!(await Drive.JsonAsync(marker, ct)).AsSpan().SequenceEqual(localBytes)) throw new GoogleConnectionException("recording_changed");
        var remoteAudio = await Drive.ChildAsync(parent, path + "/audio.m4a", "audio/mp4", ct) ?? throw new GoogleConnectionException("audio_missing");
        if (remoteAudio.Size != request.Bytes) throw new GoogleConnectionException("audio_size_mismatch");
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            long length = 0;
            await Drive.DownloadAsync(remoteAudio.Id, request.Bytes, (block, token) => { token.ThrowIfCancellationRequested(); length += block.Length; hash.AppendData(block.Span); return ValueTask.CompletedTask; }, ct);
            if (length != request.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != mobile.AudioSha256) throw new GoogleConnectionException("audio_hash_mismatch");
        }
        if (!(await Drive.JsonAsync(marker, ct)).AsSpan().SequenceEqual(localBytes)) throw new GoogleConnectionException("recording_changed");
        var transcript = store.ReadTranscript(job.Id); var notes = store.ReadNotes(job.Id); if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(notes)) throw new GoogleConnectionException("artifacts_missing");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, recording_id = mobile.RecordingId, audio_sha256 = mobile.AudioSha256, processor_id = store.ProcessorId, title = job.Title, recorded_at = mobile.RecordedAt, transcript, notes_markdown = notes });
        if (bytes.Length > GoogleWire.JsonLimit) throw new GoogleConnectionException("response_limit");
        parent = journal.FolderId!; current = "";
        foreach (var part in new[] { "results", mobile.RecordingId, mobile.AudioSha256 }) { Live(job, ct); current = current == "" ? part : current + "/" + part; parent = (await Ensure(parent, current, GoogleDriveClient.FolderMime, ct)).Id; }
        current += "/result.json"; Live(job, ct); var ensuredResult = await EnsureJson(parent, current, bytes, ct); Live(job, ct);
        var result = await Drive.FileAsync(ensuredResult.Id, ct); GoogleDriveClient.Validate(result, parent, current, "application/json"); Live(job, ct);
        if (result.Size is > 0)
        {
            var oldBytes = await Drive.JsonAsync(result, ct); if (oldBytes.AsSpan().SequenceEqual(bytes)) { Live(job, ct); await PublishSpeakers(job, parent, current[..current.LastIndexOf('/')], transcript, ct); return; }
            using var previous = GoogleWire.Strict(oldBytes); var p = previous.RootElement;
            GoogleWire.Keys(p, "schema_version", "recording_id", "audio_sha256", "processor_id", "title", "recorded_at", "transcript", "notes_markdown");
            if (p.GetProperty("schema_version").GetInt32() != 1 || p.GetProperty("recording_id").GetString() != mobile.RecordingId || p.GetProperty("audio_sha256").GetString() != mobile.AudioSha256 || p.GetProperty("processor_id").GetString() != store.ProcessorId || p.GetProperty("recorded_at").GetString() != mobile.RecordedAt) throw new GoogleConnectionException("result_conflict");
            SafeFiles.Title(p.GetProperty("title").GetString()!);
            if (string.IsNullOrWhiteSpace(p.GetProperty("transcript").GetString()) || string.IsNullOrWhiteSpace(p.GetProperty("notes_markdown").GetString())) throw new GoogleConnectionException("result_conflict");
        }
        else
        {
            if (journal.Ids.GetValueOrDefault(current) != result.Id) throw new GoogleConnectionException("foreign_empty_file");
            if (result.ETag == null) throw new GoogleConnectionException("recovery_required");
        }
        await Checked(ct); Live(job, ct); await Drive.UploadAsync(result.Id, bytes, result.ETag ?? "", ct);
        var confirmed = await Drive.FileAsync(result.Id, ct); GoogleDriveClient.Validate(confirmed, parent, current, "application/json");
        if (!(await Drive.JsonAsync(confirmed, ct)).AsSpan().SequenceEqual(bytes)) throw new GoogleConnectionException("upload_mismatch"); Live(job, ct);
        await PublishSpeakers(job, parent, current[..current.LastIndexOf('/')], transcript, ct);
    }
    async Task PublishSpeakers(RecordingJob job, string parent, string directory, string transcript, CancellationToken ct)
    {
        var document = store.ReadSpeakerDocument(job.Id); if (document == null) return;
        var serialized = document.Serialize(); var bytes = Encoding.UTF8.GetBytes(serialized);
        var path = directory + "/" + SpeakerDocument.FileName;
        void Current()
        {
            Live(job, ct);
            if (store.ReadTranscript(job.Id) != transcript || store.ReadSpeakerDocument(job.Id)?.Serialize() != serialized) throw new GoogleConnectionException("stale_job");
        }
        Current();
        var ensured = await EnsureJson(parent, path, bytes, ct);
        var file = await Drive.FileAsync(ensured.Id, ct); GoogleDriveClient.Validate(file, parent, path, "application/json"); Current();
        if (file.Size is > 0)
        {
            var old = await Drive.JsonAsync(file, ct); if (old.AsSpan().SequenceEqual(bytes)) return;
            try { SpeakerDocument.Parse(new UTF8Encoding(false, true).GetString(old), job.AudioSha256, transcript); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { throw new GoogleConnectionException("result_conflict"); }
        }
        else if (journal.Ids.GetValueOrDefault(path) != file.Id) throw new GoogleConnectionException("foreign_empty_file");
        await Checked(ct); Current();
        await Drive.UploadAsync(file.Id, bytes, file.ETag ?? "", ct);
        var confirmed = await Drive.FileAsync(file.Id, ct); GoogleDriveClient.Validate(confirmed, parent, path, "application/json");
        if (!(await Drive.JsonAsync(confirmed, ct)).AsSpan().SequenceEqual(bytes)) throw new GoogleConnectionException("upload_mismatch"); Current();
    }
    private partial void DisposeCore()
    {
        lock (sessionLock) { if (disposed) return; disposed = true; session.Cancel(); oauth?.Dispose(); session.Dispose(); }
        if (transport is IDisposable disposable) disposable.Dispose();
    }
}
