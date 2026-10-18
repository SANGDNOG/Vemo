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
        }
        public readonly Dictionary<string, Entry> Files = [];
        int counter;
        public int Writes;
        public bool LoseCreateAcknowledgment;
        public bool OmitETag;
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
            else if (uri.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct)); var p = doc.RootElement;
                var id = p.GetProperty("id").GetString()!;
                if (Files.ContainsKey(id)) { status = 409; result = new { error = "exists" }; }
                else
                {
                    var entry = new Entry(id, p.GetProperty("name").GetString()!, p.TryGetProperty("parents", out var parents) ? parents[0].GetString() : null, p.GetProperty("mimeType").GetString()!, p.GetProperty("properties").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!)); Files.Add(id, entry); Writes++; result = entry.Metadata();
                    if (LoseCreateAcknowledgment) { LoseCreateAcknowledgment = false; throw new GoogleConnectionException("transport_failed"); }
                }
            }
            else
            {
                var id = uri.AbsolutePath.Split('/')[^1];
                if (!Files.TryGetValue(id, out var entry)) { status = 404; result = new { error = "missing" }; }
                else if (request.Headers.IfMatch.Count != 0 && (request.Headers.IfMatch.Count != 1 || request.Headers.IfMatch.Single().ToString() != entry.ETag)) { status = 412; result = new { error = "precondition_failed" }; }
                else if (request.Method == HttpMethod.Patch) { entry.Bytes = await request.Content!.ReadAsByteArrayAsync(ct); Writes++; result = entry.Metadata(); }
                else if (query.GetValueOrDefault("alt") == "media") raw = entry.Bytes;
                else
                {
                    if (!OmitETag && request.Options.TryGetValue(new HttpRequestOptionsKey<Action<string?>>("Vemo.Google.ResponseETag"), out var etag)) etag(entry.ETag);
                    result = entry.Metadata();
                }
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
            if (request.Method == HttpMethod.Patch && f.Cloud.Files.TryGetValue(request.RequestUri!.AbsolutePath.Split('/')[^1], out var entry) && entry.Name == "result.json")
            {
                Assert.Single(request.Headers.IfMatch); entry.Bytes = foreign;
            }
            return Task.CompletedTask;
        };
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SyncAsync()); Assert.Equal("remote_conflict", ex.Code);
        Assert.Equal(foreign, Assert.Single(f.Cloud.Files.Values, x => x.Name == "result.json").Bytes);
    }
    [Fact] public async Task MissingETagPreventsEvenInitialEmptyFileUpload()
    {
        using var f = new Fixture(); var root = f.Cloud.Root(); f.Cloud.OmitETag = true;
        var ex = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SelectFolderAsync(root)); Assert.Equal("conditional_update_unavailable", ex.Code);
        Assert.Empty(Assert.Single(f.Cloud.Files.Values, x => x.Name == "processor.json").Bytes);
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
}
