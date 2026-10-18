using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class GoogleDriveTests
{
    sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath = Path.Combine((OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath() : Path.GetTempPath()), "vemo-google-test-" + Guid.NewGuid());
        public LibraryStore Store { get; }
        public GoogleMemoryVault Vault { get; } = new();
        public Cloud Cloud { get; }
        public GoogleConnectionService Service { get; }
        public Fixture()
        {
            Store = new(DirectoryPath); Vault.Save("configuration", GoogleOAuthTests.Configuration); Vault.Save("refresh", "fixture-refresh");
            Cloud = new(Store.ProcessorId); Service = new(Store, Vault, Cloud);
        }
        public void Dispose() { Service.Dispose(); Store.Dispose(); Directory.Delete(DirectoryPath, true); }
    }
    sealed class Cloud(string processor) : IGoogleHttpTransport
    {
        public sealed class Entry(string id, string name, string? parent, string mime, Dictionary<string, string> properties, byte[]? bytes = null)
        {
            public string Id = id, Name = name, Mime = mime;
            public string? Parent = parent;
            public Dictionary<string, string> Properties = properties;
            public byte[] Bytes = bytes ?? [];
            public string ETag => "\"" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { metadata = Metadata(), content = Convert.ToBase64String(Bytes) }))) + "\"";
            public object Metadata() => new { id = Id, name = Name, mimeType = Mime, parents = Parent == null ? Array.Empty<string>() : new[] { Parent }, properties = Properties, trashed = false, size = Bytes.Length.ToString() };
            public object MetadataV2(bool omitETag) => new { id = Id, title = Name, mimeType = Mime, parents = Parent == null ? [] : new[] { new { id = Parent } }, properties = Properties.Select(x => new { key = x.Key, value = x.Value, visibility = "PUBLIC" }).ToArray(), labels = new { trashed = false }, fileSize = Bytes.Length.ToString(), etag = omitETag ? null : ETag };
        }
        public readonly Dictionary<string, Entry> Files = [];
        int counter;
        public int Writes;
        public bool LoseCreateAcknowledgment;
        public bool OmitETag;
        public bool LoseMultipartAcknowledgment;
        public int MultipartCreates;
        public int MediaUpdates;
        public string Account = "account-fixture";
        public Func<HttpRequestMessage, CancellationToken, Task>? Before;
        public string Root(string name = "Vemo", string? owner = null)
        {
            var id = "root-" + ++counter; Files.Add(id, new(id, name, null, GoogleDriveClient.FolderMime, new() { ["vemoProtocol"] = "1", ["vemoKind"] = "root", ["vemoProcessor"] = owner ?? processor })); return id;
        }
        public string Add(string parent, string path, string mime, byte[]? bytes = null)
        {
            var id = "file-" + ++counter; Files.Add(id, new(id, path.Split('/')[^1], parent, mime, GoogleDriveClient.PropertiesFor(path), bytes)); return id;
        }
        public (string Id, string Hash, string Audio, string Marker) Recording(string root, string? target = null, bool goodHash = true)
        {
            var id = Guid.NewGuid().ToString(); var audio = Encoding.UTF8.GetBytes("fixture original audio"); var hash = Convert.ToHexStringLower(SHA256.HashData(audio));
            var recordings = Files.Values.SingleOrDefault(x => x.Parent == root && x.Name == "recordings")?.Id ?? Add(root, "recordings", GoogleDriveClient.FolderMime);
            var folder = Add(recordings, "recordings/" + id, GoogleDriveClient.FolderMime); var path = "recordings/" + id + "/" + hash; var revision = Add(folder, path, GoogleDriveClient.FolderMime);
            var audioId = Add(revision, path + "/audio.m4a", "audio/mp4", goodHash ? audio : Encoding.UTF8.GetBytes("changed original audio"));
            var marker = Add(revision, path + "/recording.json", "application/json", JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, recording_id = id, audio_sha256 = hash, audio_bytes = Files[audioId].Bytes.Length, audio_file = "audio.m4a", recorded_at = "2026-10-18T12:00:00Z", title = "Phone recording", mode = "desktop", target_processor_id = target ?? processor, completed = true })); return (id, hash, audioId, marker);
        }
        public async Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
        {
            if (Before != null) await Before(request, ct);
            ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!; object? result = null; int status = 200; byte[]? raw = null;
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            if (uri.Host == "oauth2.googleapis.com") result = new { access_token = "fixture-access", token_type = "Bearer", expires_in = 3600 };
            else if (uri.AbsolutePath.EndsWith("/about")) result = new { user = new { permissionId = Account } };
            else if (uri.AbsolutePath.EndsWith("/generateIds")) result = new { ids = new[] { "generated-" + ++counter } };
            else if (uri.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Get)
            {
                var q = query["q"]; var candidates = q.StartsWith("'", StringComparison.Ordinal) ? Files.Values.Where(x => x.Parent == q.Split('\'')[1]) : Files.Values.Where(x => x.Properties.GetValueOrDefault("vemoKind") == "root");
                result = new { files = candidates.Select(x => x.Metadata()).ToArray(), incompleteSearch = false };
            }
            else if ((uri.AbsolutePath == "/drive/v3/files" || uri.AbsolutePath == "/upload/drive/v3/files") && request.Method == HttpMethod.Post)
            {
                byte[]? media = null; byte[] metadata;
                if (request.Content is MultipartContent multipart)
                {
                    Assert.Equal("related", multipart.Headers.ContentType!.MediaType!.Split('/')[1]); Assert.Equal("multipart", query["uploadType"]);
                    var parts = multipart.ToArray(); Assert.Equal(2, parts.Length);
                    Assert.Equal("application/json", parts[0].Headers.ContentType!.MediaType); Assert.Equal("application/json", parts[1].Headers.ContentType!.MediaType);
                    metadata = await parts[0].ReadAsByteArrayAsync(ct); media = await parts[1].ReadAsByteArrayAsync(ct); MultipartCreates++;
                }
                else metadata = await request.Content!.ReadAsByteArrayAsync(ct);
                using var doc = JsonDocument.Parse(metadata); var p = doc.RootElement;
                var id = p.GetProperty("id").GetString()!;
                if (Files.ContainsKey(id)) { status = 409; result = new { error = "exists" }; }
                else
                {
                    var entry = new Entry(id, p.GetProperty("name").GetString()!, p.TryGetProperty("parents", out var parents) ? parents[0].GetString() : null, p.GetProperty("mimeType").GetString()!, p.GetProperty("properties").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!)); entry.Bytes = media ?? []; Files.Add(id, entry); Writes++; result = entry.Metadata();
                    if (media != null && LoseMultipartAcknowledgment) { LoseMultipartAcknowledgment = false; throw new GoogleConnectionException("transport_failed"); }
                    if (LoseCreateAcknowledgment) { LoseCreateAcknowledgment = false; throw new GoogleConnectionException("transport_failed"); }
                }
            }
            else
            {
                var id = uri.AbsolutePath.Split('/')[^1];
                if (!Files.TryGetValue(id, out var entry)) { status = 404; result = new { error = "missing" }; }
                else if (request.Method == HttpMethod.Put && uri.AbsolutePath == "/upload/drive/v2/files/" + id)
                {
                    Assert.Equal("media", query["uploadType"]); Assert.Single(request.Headers.IfMatch);
                    if (request.Headers.IfMatch.Single().ToString() != entry.ETag) { status = 412; result = new { error = "precondition_failed" }; }
                    else { entry.Bytes = await request.Content!.ReadAsByteArrayAsync(ct); Writes++; MediaUpdates++; result = entry.MetadataV2(OmitETag); }
                }
                // v3 media PATCH and GET do not provide the conditional guarantee we need.
                else if (request.Method == HttpMethod.Patch) throw new InvalidOperationException("Conditional writes must use Drive v2 PUT.");
                else if (query.GetValueOrDefault("alt") == "media") raw = entry.Bytes;
                else result = uri.AbsolutePath == "/drive/v2/files/" + id ? entry.MetadataV2(OmitETag) : entry.Metadata();
            }
            raw ??= JsonSerializer.SerializeToUtf8Bytes(result); await receive(raw, ct); return status;
        }
    }
    [Fact] public void DefaultServiceWithoutStoredCredentialsIsHeadlessSafe()
    {
        var path = Path.Combine((OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath() : Path.GetTempPath()), "vemo-no-google-" + Guid.NewGuid());
        try
        {
            using var store = new LibraryStore(path);
            var shared = GoogleConnectionService.Shared(store); Assert.Same(shared, GoogleConnectionService.Shared(store)); shared.Dispose();
            using var service = new GoogleConnectionService(store, new GoogleMemoryVault(), new GoogleFixtureTransport((_, _) => throw new InvalidOperationException("No network expected.")), () => null);
            Assert.False(service.Status.Configured); Assert.False(service.Status.Connected); Assert.Null(service.Status.LastError);
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact] public async Task CreateBindsAndHydratesWithoutChangingMobileDirectory()
    {
        using var f = new Fixture(); var mobile = f.Store.Settings.MobileDirectory;
        Assert.True(f.Service.Status.Configured); Assert.True(f.Service.Status.Connected);
        var folder = await f.Service.CreateFolderAsync("Vemo Windows"); Assert.Equal(folder.Id, f.Service.Status.FolderId); Assert.Equal(mobile, f.Store.Settings.MobileDirectory);
        using var restored = new GoogleConnectionService(f.Store, f.Vault, f.Cloud); Assert.True(restored.Status.Connected); Assert.Equal(folder.Id, restored.Status.FolderId);
        await restored.DisconnectAsync(); Assert.False(restored.Status.Connected); Assert.Equal(folder.Id, restored.Status.FolderId); Assert.Null(f.Vault.Load("refresh"));
    }
    [Fact] public async Task SelectWrongProcessorAndDuplicateRootsAreRejected()
    {
        using var f = new Fixture(); var wrong = f.Cloud.Root(owner: Guid.NewGuid().ToString());
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SelectFolderAsync(wrong)); Assert.Equal("processor_mismatch", ex.Code); Assert.Null(f.Service.Status.FolderId); Assert.Equal(0, f.Cloud.Writes);
        f.Cloud.Root(); ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.ListFoldersAsync()); Assert.Equal("duplicate_roots", ex.Code);
    }
    [Fact] public async Task LostCreateAcknowledgmentReusesDurablyReservedId()
    {
        using var f = new Fixture(); f.Cloud.LoseCreateAcknowledgment = true;
        await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.CreateFolderAsync("Vemo"));
        var created = await f.Service.CreateFolderAsync("Vemo"); Assert.Single(f.Cloud.Files.Values, x => x.Parent == null); Assert.Equal(created.Id, f.Service.Status.FolderId);
    }
    [Fact] public async Task ImportsExactOriginalPublishesCompletedResultAndReconnectPreservesIdentity()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); var recording = f.Cloud.Recording(root); var original = f.Cloud.Files[recording.Audio].Bytes.ToArray();
        await f.Service.SelectFolderAsync(root); var sync = await f.Service.SyncAsync(); Assert.Equal(1, sync.Imported);
        var job = Assert.Single(f.Store.Jobs); Assert.Equal(original, File.ReadAllBytes(f.Store.AudioPath(job.Id))); Assert.Equal("", f.Store.Settings.MobileDirectory);
        Assert.True(f.Store.TryStart(job.Id)); f.Store.SaveTranscript(job.Id, "transcript fixture"); f.Store.Complete(job.Id, "# Notes");
        sync = await f.Service.SyncAsync(); Assert.Equal(1, sync.Published); var result = Assert.Single(f.Cloud.Files.Values, x => x.Name == "result.json");
        using var json = JsonDocument.Parse(result.Bytes); Assert.Equal(f.Store.ProcessorId, json.RootElement.GetProperty("processor_id").GetString()); Assert.Equal("transcript fixture", json.RootElement.GetProperty("transcript").GetString());
        var writes = f.Cloud.Writes; await f.Service.SyncAsync(); Assert.Equal(writes, f.Cloud.Writes); Assert.Equal(original, f.Cloud.Files[recording.Audio].Bytes);
        await f.Service.DisconnectAsync(); f.Vault.Save("refresh", "reconnected-fixture"); using var restored = new GoogleConnectionService(f.Store, f.Vault, f.Cloud); await restored.SyncAsync(); Assert.Single(f.Store.Jobs);
    }
    [Fact] public async Task OwnResultCanUpdateButForeignProcessorResultIsPreserved()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); f.Cloud.Recording(root); await f.Service.SelectFolderAsync(root); await f.Service.SyncAsync();
        var job = Assert.Single(f.Store.Jobs); f.Store.TryStart(job.Id); f.Store.SaveTranscript(job.Id, "transcript"); f.Store.Complete(job.Id, "notes"); await f.Service.SyncAsync();
        f.Store.Rename(job.Id, "Updated title"); await f.Service.SyncAsync(); var result = Assert.Single(f.Cloud.Files.Values, x => x.Name == "result.json");
        using (var doc = JsonDocument.Parse(result.Bytes)) Assert.Equal("Updated title", doc.RootElement.GetProperty("title").GetString());
        result.Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(result.Bytes).Replace(f.Store.ProcessorId, Guid.NewGuid().ToString()));
        var foreign = result.Bytes.ToArray(); var writes = f.Cloud.Writes;
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync()); Assert.Equal("result_conflict", ex.Code); Assert.Equal(foreign, result.Bytes); Assert.Equal(writes, f.Cloud.Writes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalResultPublicationPreservesConcurrentForeignContent(bool existingResult)
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); f.Cloud.Recording(root); await f.Service.SelectFolderAsync(root); await f.Service.SyncAsync();
        var job = Assert.Single(f.Store.Jobs); f.Store.TryStart(job.Id); f.Store.SaveTranscript(job.Id, "transcript"); f.Store.Complete(job.Id, "notes");
        if (existingResult) { await f.Service.SyncAsync(); f.Store.Rename(job.Id, "Renamed"); }
        var foreign = Encoding.UTF8.GetBytes("concurrent foreign content");
        f.Cloud.Before = (request, _) =>
        {
            if (request.Method == HttpMethod.Put && f.Cloud.Files.TryGetValue(request.RequestUri!.AbsolutePath.Split('/')[^1], out var entry) && entry.Name == "result.json")
            {
                Assert.Single(request.Headers.IfMatch); entry.Bytes = foreign;
            }
            if (!existingResult && request.Content is MultipartContent multipart)
            {
                using var doc = JsonDocument.Parse(multipart.First().ReadAsByteArrayAsync().GetAwaiter().GetResult()); var p = doc.RootElement;
                if (p.GetProperty("name").GetString() == "result.json")
                {
                    var id = p.GetProperty("id").GetString()!;
                    f.Cloud.Files[id] = new Cloud.Entry(id, "result.json", p.GetProperty("parents")[0].GetString(), "application/json", p.GetProperty("properties").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!), foreign);
                }
            }
            return Task.CompletedTask;
        };
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync()); Assert.Equal(existingResult ? "remote_conflict" : "result_conflict", ex.Code);
        Assert.Equal(foreign, Assert.Single(f.Cloud.Files.Values, x => x.Name == "result.json").Bytes);
    }
    [Fact] public async Task NoETagFreshRootAndFirstResultUseAtomicMultipartAndUpdatesFailClosed()
    {
        using var f = new Fixture(); f.Cloud.OmitETag = true;
        var root = await f.Service.CreateFolderAsync(""); Assert.Equal("Vemo Windows", root.Name);
        Assert.NotEmpty(Assert.Single(f.Cloud.Files.Values, x => x.Name == "processor.json").Bytes);
        f.Cloud.Recording(root.Id); await f.Service.SyncAsync(); var job = Assert.Single(f.Store.Jobs);
        f.Store.TryStart(job.Id); f.Store.SaveTranscript(job.Id, "transcript"); f.Store.Complete(job.Id, "notes");
        await f.Service.SyncAsync(); Assert.Equal(2, f.Cloud.MultipartCreates); Assert.Equal(0, f.Cloud.MediaUpdates);
        var result = Assert.Single(f.Cloud.Files.Values, x => x.Name == "result.json"); var original = result.Bytes.ToArray();
        await f.Service.SyncAsync(); Assert.Equal(2, f.Cloud.MultipartCreates);
        f.Store.Rename(job.Id, "Renamed"); var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync());
        Assert.Equal("conditional_update_unavailable", ex.Code); Assert.Equal(original, result.Bytes); Assert.Equal(0, f.Cloud.MediaUpdates);
    }
    [Fact] public async Task ConflictingFirstPublicationDoesNotBlockNextAndRetriesDoNotDuplicate()
    {
        using var f = new Fixture(); f.Cloud.OmitETag = true;
        var root = f.Cloud.Root(); f.Cloud.Recording(root); f.Cloud.Recording(root);
        await f.Service.SelectFolderAsync(root); await f.Service.SyncAsync();
        var jobs = f.Store.Jobs.ToArray(); Assert.Equal(2, jobs.Length);
        foreach (var job in jobs) { Assert.True(f.Store.TryStart(job.Id)); f.Store.SaveTranscript(job.Id, "live transcript"); f.Store.Complete(job.Id, "live notes"); }
        // Seed only the first job's old fixture result; the second job is still eligible for its first publication.
        var first = jobs[0]; var mobile = first.Mobile!;
        var results = f.Cloud.Add(root, "results", GoogleDriveClient.FolderMime);
        var recording = f.Cloud.Add(results, "results/" + mobile.RecordingId, GoogleDriveClient.FolderMime);
        var path = "results/" + mobile.RecordingId + "/" + mobile.AudioSha256;
        var revision = f.Cloud.Add(recording, path, GoogleDriveClient.FolderMime);
        var oldBytes = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, recording_id = mobile.RecordingId, audio_sha256 = mobile.AudioSha256, processor_id = f.Store.ProcessorId, title = first.Title, recorded_at = mobile.RecordedAt, transcript = "old fixture transcript", notes_markdown = "old fixture notes" });
        var oldResult = f.Cloud.Add(revision, path + "/result.json", "application/json", oldBytes);
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync());
        Assert.Equal("conditional_update_unavailable", ex.Code); Assert.Equal(ex.Code, f.Service.Status.LastError); Assert.False(f.Service.Status.Busy);
        Assert.Equal(oldBytes, f.Cloud.Files[oldResult].Bytes); Assert.Equal(0, f.Cloud.MediaUpdates);
        var fresh = Assert.Single(f.Cloud.Files.Values, x => x.Name == "result.json" && x.Id != oldResult);
        using (var doc = JsonDocument.Parse(fresh.Bytes))
        {
            Assert.Equal(jobs[1].Mobile!.RecordingId, doc.RootElement.GetProperty("recording_id").GetString());
            Assert.Equal("live transcript", doc.RootElement.GetProperty("transcript").GetString());
        }
        var writes = f.Cloud.Writes;
        ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync());
        Assert.Equal("conditional_update_unavailable", ex.Code); Assert.Equal(writes, f.Cloud.Writes);
        Assert.Equal(2, f.Store.Jobs.Count); Assert.Equal(2, f.Cloud.Files.Values.Count(x => x.Name == "result.json"));
        Assert.Equal(oldBytes, f.Cloud.Files[oldResult].Bytes);
        // Cancellation in the next publication must take precedence over the deferred conflict.
        using var cancellation = new CancellationTokenSource(); var reachedSecond = false;
        var secondAudio = f.Cloud.Files.Values.Single(x => x.Name == "audio.m4a" && x.Properties.GetValueOrDefault("vemoKey") == GoogleDriveClient.PathKey("recordings/" + jobs[1].Mobile!.RecordingId + "/" + jobs[1].Mobile!.AudioSha256 + "/audio.m4a"));
        f.Cloud.Before = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/" + secondAudio.Id) && request.RequestUri.Query.Contains("alt=media", StringComparison.Ordinal))
            { reachedSecond = true; cancellation.Cancel(); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SyncAsync(cancellation.Token));
        Assert.True(reachedSecond); Assert.Equal("cancelled", f.Service.Status.LastError);
        Assert.Equal(writes, f.Cloud.Writes); Assert.False(f.Service.Status.Busy);
    }
    [Theory]
    [InlineData("cancelled")]
    [InlineData("reauthorization_required")]
    [InlineData("account_mismatch")]
    [InlineData("invalid_root")]
    [InlineData("transport_failed")]
    public async Task GlobalPublicationFailureStopsBatchImmediately(string code)
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); f.Cloud.Recording(root); f.Cloud.Recording(root);
        await f.Service.SelectFolderAsync(root); await f.Service.SyncAsync();
        foreach (var job in f.Store.Jobs) { Assert.True(f.Store.TryStart(job.Id)); f.Store.SaveTranscript(job.Id, "transcript"); f.Store.Complete(job.Id, "notes"); }
        using var cancellation = new CancellationTokenSource(); var attempts = 0;
        f.Cloud.Before = (request, _) =>
        {
            if (request.Content is MultipartContent multipart)
            {
                using var doc = JsonDocument.Parse(multipart.First().ReadAsByteArrayAsync().GetAwaiter().GetResult());
                if (doc.RootElement.GetProperty("name").GetString() == "result.json")
                {
                    attempts++;
                    if (code == "cancelled") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                    throw new GoogleConnectionException(code);
                }
            }
            return Task.CompletedTask;
        };
        if (code == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SyncAsync(cancellation.Token));
        else Assert.Equal(code, (await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync())).Code);
        Assert.Equal(1, attempts); Assert.Equal(code, f.Service.Status.LastError); Assert.False(f.Service.Status.Busy);
        Assert.DoesNotContain(f.Cloud.Files.Values, x => x.Name == "result.json");
    }
    [Fact] public async Task ListsOnlyOwnProcessorAndMultipartLostAcknowledgmentReusesReservation()
    {
        using var f = new Fixture(); f.Cloud.OmitETag = true; var foreign = f.Cloud.Root("Vemo 테스트", Guid.NewGuid().ToString());
        Assert.Empty(await f.Service.ListFoldersAsync()); Assert.Null(f.Service.Status.FolderId);
        f.Cloud.LoseMultipartAcknowledgment = true;
        await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.CreateFolderAsync(""));
        var folder = await f.Service.CreateFolderAsync(""); Assert.Equal(folder.Id, Assert.Single(await f.Service.ListFoldersAsync()).Id);
        Assert.Single(f.Cloud.Files.Values, x => x.Name == "processor.json"); Assert.Equal(0, f.Cloud.MediaUpdates);
        Assert.NotEqual(f.Store.ProcessorId, f.Cloud.Files[foreign].Properties["vemoProcessor"]);
    }
    [Fact] public async Task DefaultNamesEnumerateCollisionsAndRetryKeepsReservedName()
    {
        using var f = new Fixture(); f.Cloud.OmitETag = true;
        f.Cloud.Root("Vemo Windows", Guid.NewGuid().ToString()); f.Cloud.Root("Vemo Windows (2)", Guid.NewGuid().ToString());
        f.Cloud.LoseCreateAcknowledgment = true;
        await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.CreateFolderAsync("Vemo Windows"));
        var folder = await f.Service.CreateFolderAsync(""); Assert.Equal("Vemo Windows (3)", folder.Name);
        Assert.Equal(folder.Id, Assert.Single(await f.Service.ListFoldersAsync()).Id);
        Assert.Single(f.Cloud.Files.Values, x => x.Name == "Vemo Windows (3)");
    }
    [Fact] public async Task MultipartDuplicateIdConfirmsExactBytesAndPreservesConflictingContent()
    {
        using var f = new Fixture(); f.Cloud.OmitETag = true; var root = f.Cloud.Root();
        using var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(GoogleOAuthTests.Configuration), f.Vault, f.Cloud);
        var drive = new GoogleDriveClient(oauth, f.Cloud); var id = await drive.AllocateIdAsync(); var bytes = Encoding.UTF8.GetBytes("{\"fixture\":true}");
        await drive.CreateJsonAsync(id, root, "fixture.json", bytes); var writes = f.Cloud.Writes;
        await drive.CreateJsonAsync(id, root, "fixture.json", bytes); Assert.Equal(writes, f.Cloud.Writes);
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => drive.CreateJsonAsync(id, root, "fixture.json", Encoding.UTF8.GetBytes("{}")));
        Assert.Equal("result_conflict", ex.Code); Assert.Equal(bytes, f.Cloud.Files[id].Bytes); Assert.Equal(writes, f.Cloud.Writes);
    }
    [Fact] public async Task PriorEmptyProcessorWithoutETagIsPreserved()
    {
        using var f = new Fixture(); f.Cloud.OmitETag = true; var root = f.Cloud.Root(); f.Cloud.Add(root, "processor.json", "application/json");
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SelectFolderAsync(root));
        Assert.Equal("foreign_empty_file", ex.Code); Assert.Empty(Assert.Single(f.Cloud.Files.Values, x => x.Name == "processor.json").Bytes); Assert.Equal(0, f.Cloud.MediaUpdates);
    }
    [Fact] public async Task WrongTargetIsSkippedAndBadAudioHashNeverImports()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); f.Cloud.Recording(root, Guid.NewGuid().ToString());
        await f.Service.SelectFolderAsync(root); Assert.Equal(1, (await f.Service.SyncAsync()).Skipped); Assert.Empty(f.Store.Jobs);
        f.Cloud.Recording(root, goodHash: false); var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync()); Assert.Equal("audio_hash_mismatch", ex.Code); Assert.Empty(f.Store.Jobs);
    }
    [Fact] public async Task RemoteAudioChangedAfterImportBlocksResultPublication()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); var recording = f.Cloud.Recording(root); await f.Service.SelectFolderAsync(root); await f.Service.SyncAsync();
        var job = Assert.Single(f.Store.Jobs); f.Store.TryStart(job.Id); f.Store.SaveTranscript(job.Id, "transcript"); f.Store.Complete(job.Id, "notes");
        var audio = f.Cloud.Files[recording.Audio]; audio.Bytes = Enumerable.Repeat((byte)65, audio.Bytes.Length).ToArray();
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync()); Assert.Equal("audio_hash_mismatch", ex.Code); Assert.DoesNotContain(f.Cloud.Files.Values, x => x.Name == "result.json");
    }
    [Fact] public async Task ForeignSameNameMetadataIsPreservedAndNeverOverwritten()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); var file = f.Cloud.Add(root, "processor.json", "application/json", Encoding.UTF8.GetBytes("foreign")); f.Cloud.Files[file].Properties.Clear();
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SelectFolderAsync(root)); Assert.Equal("invalid_metadata", ex.Code); Assert.Equal(0, f.Cloud.Writes); Assert.Equal("foreign", Encoding.UTF8.GetString(f.Cloud.Files[file].Bytes));
    }
    [Fact] public async Task DuplicateLogicalPathFailsClosed()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); f.Cloud.Add(root, "processor.json", "application/json"); f.Cloud.Add(root, "processor.json", "application/json");
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SelectFolderAsync(root)); Assert.Equal("duplicate_path", ex.Code); Assert.Equal(0, f.Cloud.Writes);
    }
    [Fact] public async Task AccountSwitchCannotReadOrPublishBoundFolder()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); await f.Service.SelectFolderAsync(root); f.Cloud.Account = "different-account"; var writes = f.Cloud.Writes;
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync()); Assert.Equal("account_mismatch", ex.Code); Assert.Equal(writes, f.Cloud.Writes);
    }
    [Fact] public async Task CancellationAfterRemoteSelectionAwaitDoesNotPersistBinding()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); using var ct = new CancellationTokenSource();
        f.Cloud.Before = (request, _) => { if (request.RequestUri!.AbsolutePath.EndsWith("/about")) ct.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SelectFolderAsync(root, ct.Token)); Assert.Null(f.Service.Status.FolderId); Assert.Equal(0, f.Cloud.Writes);
    }
    [Fact] public async Task StopCancelsAndDrainsThenAllowsLaterOperations()
    {
        using var f = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cloud.Before = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var pending = f.Service.ListFoldersAsync(); await entered.Task; await f.Service.StopAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.False(f.Service.Status.Busy);
        f.Cloud.Before = null; Assert.Empty(await f.Service.ListFoldersAsync());
    }
    [Fact] public async Task OptionalSpeakerRenameUsesConditionalUpdateAndKeepsV1ResultExact()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); f.Cloud.Recording(root);
        await f.Service.SelectFolderAsync(root); await f.Service.SyncAsync();
        var job = Assert.Single(f.Store.Jobs); f.Store.TryStart(job.Id); f.Store.SaveTranscript(job.Id, "original", [new(0, 2, "original")]); f.Store.Complete(job.Id, "notes");
        await f.Service.SyncAsync(); var result = Assert.Single(f.Cloud.Files.Values, x => x.Name == "result.json"); var original = result.Bytes.ToArray();
        var document = SpeakerDocument.Create(job.AudioSha256, f.Store.ReadTranscriptDocument(job.Id), [new(0, 2, "raw")]); f.Store.SaveSpeakerDocument(job.Id, document);
        await f.Service.SyncAsync(); var sidecar = Assert.Single(f.Cloud.Files.Values, x => x.Name == "speakers.json");
        f.Store.RenameSpeaker(job.Id, document.Generation, "speaker_1", "진행자");
        var updates = f.Cloud.MediaUpdates; await f.Service.SyncAsync(); Assert.Equal(updates + 1, f.Cloud.MediaUpdates);
        Assert.Equal(original, result.Bytes); Assert.Equal("진행자", SpeakerDocument.Parse(Encoding.UTF8.GetString(sidecar.Bytes), job.AudioSha256, "original").Speakers[0].Name);
        f.Store.RenameSpeaker(job.Id, document.Generation, "speaker_1", "changed again"); var saved = sidecar.Bytes.ToArray();
        f.Cloud.Before = (request, _) => { if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.EndsWith("/" + sidecar.Id)) sidecar.Properties["race"] = "changed"; return Task.CompletedTask; };
        Assert.Equal("remote_conflict", (await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync())).Code);
        Assert.Equal(saved, sidecar.Bytes); Assert.Equal(original, result.Bytes);
    }
}
