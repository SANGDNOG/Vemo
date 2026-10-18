using System.Text;
using System.Text.Json;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

// Independent regressions for folder choices and first-run creation; no live Google data.
public sealed class GoogleFolderEligibilityIndependentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listed_choices_are_only_current_PC_roots_without_mutating_foreign_roots(bool includeEligible)
    {
        using var f = new Fixture();
        f.Cloud.Root("foreign-root", "Other PC", Guid.NewGuid().ToString());
        if (includeEligible) f.Cloud.Root("eligible-root", "This PC", f.Store.ProcessorId);
        var before = f.Cloud.Snapshot();
        var listed = await f.Service.ListFoldersAsync();
        Assert.Equal(includeEligible ? new[] { "eligible-root" } : [], listed.Select(x => x.Id));
        Assert.All(listed, x => Assert.Equal(f.Store.ProcessorId, x.ProcessorId));
        Assert.Equal(before, f.Cloud.Snapshot()); Assert.Equal(0, f.Cloud.Writes);
        Assert.Null(f.Service.Status.FolderId); Assert.False(File.Exists(f.Binding));
        Assert.Equal("fixture-refresh", f.Vault.Load("refresh")); Assert.Equal(0, f.Vault.Mutations);
    }

    [Fact]
    public async Task Stale_selection_rechecks_owner_before_remote_or_local_binding_mutation()
    {
        using var f = new Fixture();
        var root = f.Cloud.Root("changing-root", "Previously eligible", f.Store.ProcessorId);
        Assert.Equal(root.Id, Assert.Single(await f.Service.ListFoldersAsync()).Id);
        root.Properties["vemoProcessor"] = Guid.NewGuid().ToString();
        var before = f.Cloud.Snapshot();
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => f.Service.SelectFolderAsync(root.Id));
        Assert.Equal("processor_mismatch", error.Code);
        Assert.Equal(before, f.Cloud.Snapshot()); Assert.Equal(0, f.Cloud.Writes);
        Assert.Null(f.Service.Status.FolderId); Assert.False(File.Exists(f.Binding));
        Assert.True(f.Service.Status.Connected); Assert.False(f.Service.Status.Busy);
        Assert.Equal("fixture-refresh", f.Vault.Load("refresh")); Assert.Equal(0, f.Vault.Mutations);
    }

    [Fact]
    public async Task Fresh_PC_creates_complete_processor_metadata_atomically_without_ETag_or_touching_foreign_root()
    {
        using var f = new Fixture();
        var foreign = f.Cloud.Root("foreign-root", "Other PC", Guid.NewGuid().ToString());
        var foreignBefore = foreign.Snapshot();
        var created = await f.Service.CreateFolderAsync("New PC");
        Assert.Equal(f.Store.ProcessorId, created.ProcessorId);
        Assert.Equal(created.Id, f.Service.Status.FolderId);
        var processor = Assert.Single(f.Cloud.Entries.Values, x => x.Name == "processor.json");
        Assert.Equal(created.Id, processor.Parent);
        Assert.Equal(foreignBefore, foreign.Snapshot());
        Assert.DoesNotContain(f.Cloud.Creates, x => x.Name == "processor.json" && x.InitialContent.Length == 0);
        var write = Assert.Single(f.Cloud.Creates, x => x.Name == "processor.json");
        using var owner = JsonDocument.Parse(write.InitialContent);
        Assert.Equal(1, owner.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(f.Store.ProcessorId, owner.RootElement.GetProperty("processor_id").GetString());
        Assert.Equal(write.InitialContent, processor.Bytes); Assert.Equal(0, f.Cloud.Patches);
        Assert.Equal("fixture-refresh", f.Vault.Load("refresh")); Assert.Equal(0, f.Vault.Mutations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("W/\"fixture-version\"")]
    public async Task Existing_content_cannot_be_overwritten_without_strong_ETag(string etag)
    {
        var vault = new Vault(); var cloud = new Cloud();
        var existing = cloud.Root("existing", "Keep original", Guid.NewGuid().ToString());
        existing.Bytes = Encoding.UTF8.GetBytes("existing fixture content");
        var before = cloud.Snapshot();
        using var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(Vault.Configuration), vault, cloud);
        var drive = new GoogleDriveClient(oauth, cloud);
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => drive.UploadAsync(existing.Id, "replacement"u8.ToArray(), etag));
        Assert.Equal("conditional_update_unavailable", error.Code);
        Assert.Equal(0, cloud.Requests); Assert.Equal(0, cloud.Writes);
        Assert.Equal(before, cloud.Snapshot()); Assert.Equal(0, vault.Mutations);
    }

    sealed class Fixture : IDisposable
    {
        public LibraryStore Store { get; }
        public Vault Vault { get; } = new();
        public Cloud Cloud { get; } = new();
        public GoogleConnectionService Service { get; }
        public string Binding => Path.Combine(Store.Root, "google", "binding.json");
        public Fixture()
        {
            var path = Path.Combine(Path.GetTempPath(), "vemo-folder-independent-" + Guid.NewGuid()).Replace("/var/", "/private/var/");
            Store = new(path); Service = new(Store, Vault, Cloud, () => null);
        }
        public void Dispose() { Service.Dispose(); var path = Store.Root; Store.Dispose(); Directory.Delete(path, true); }
    }
    sealed class Vault : IGoogleCredentialVault
    {
        public const string Configuration = "{\"installed\":{\"client_id\":\"folder-fixture.apps.googleusercontent.com\"}}";
        readonly Dictionary<string, string> values = new() { ["configuration"] = Configuration, ["refresh"] = "fixture-refresh" };
        public int Mutations;
        public string? Load(string key) => values.GetValueOrDefault(key);
        public void Save(string key, string value) { Mutations++; values[key] = value; }
        public void Delete(string key) { Mutations++; values.Remove(key); }
    }
    sealed class Cloud : IGoogleHttpTransport
    {
        public sealed class Entry(string id, string name, string mime, string? parent, Dictionary<string, string> properties, byte[]? content = null)
        {
            public string Id = id, Name = name, Mime = mime;
            public string? Parent = parent;
            public Dictionary<string, string> Properties = properties;
            public byte[] Bytes = content ?? [];
            public object Metadata() => new { id = Id, name = Name, mimeType = Mime, parents = Parent == null ? Array.Empty<string>() : [Parent], properties = Properties, trashed = false, size = Bytes.Length.ToString() };
            public object MetadataV2() => new { id = Id, title = Name, mimeType = Mime, parents = Parent == null ? [] : new[] { new { id = Parent } }, properties = Properties.Select(x => new { key = x.Key, value = x.Value, visibility = "PUBLIC" }).ToArray(), labels = new { trashed = false }, fileSize = Bytes.Length.ToString() };
            public string Snapshot() => JsonSerializer.Serialize(Metadata()) + Convert.ToBase64String(Bytes);
        }
        public Dictionary<string, Entry> Entries { get; } = [];
        public List<(string Name, byte[] InitialContent)> Creates { get; } = [];
        public int Requests, Writes, Patches;
        int next;
        public Entry Root(string id, string name, string owner)
        {
            var entry = new Entry(id, name, GoogleDriveClient.FolderMime, null, new() { ["vemoProtocol"] = "1", ["vemoKind"] = "root", ["vemoProcessor"] = owner });
            Entries.Add(id, entry); return entry;
        }
        public string Snapshot() => string.Join("\n", Entries.OrderBy(x => x.Key).Select(x => x.Value.Snapshot()));
        public async Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
        {
            Requests++; ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!;
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            object result; byte[]? raw = null; var status = 200;
            if (uri.Host == "oauth2.googleapis.com") result = new { access_token = "fixture-access", token_type = "Bearer", expires_in = 3600 };
            else if (uri.AbsolutePath.EndsWith("/about")) result = new { user = new { permissionId = "fixture-account" } };
            else if (uri.AbsolutePath.EndsWith("/generateIds")) result = new { ids = new[] { "generated-" + ++next } };
            else if (uri.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Get)
            {
                var q = query["q"];
                var entries = q.StartsWith("'", StringComparison.Ordinal) ? Entries.Values.Where(x => x.Parent == q.Split('\'')[1]) : Entries.Values.Where(x => x.Properties.GetValueOrDefault("vemoKind") == "root");
                result = new { files = entries.Select(x => x.Metadata()).ToArray(), incompleteSearch = false };
            }
            else if (request.Method == HttpMethod.Post && uri.AbsolutePath is "/drive/v3/files" or "/upload/drive/v3/files")
            {
                var (metadata, content) = await CreationParts(request.Content!, ct);
                using var doc = JsonDocument.Parse(metadata); var p = doc.RootElement; var id = p.GetProperty("id").GetString()!;
                if (Entries.ContainsKey(id)) { status = 409; result = new { error = "exists" }; }
                else
                {
                    var entry = new Entry(id, p.GetProperty("name").GetString()!, p.GetProperty("mimeType").GetString()!, p.TryGetProperty("parents", out var parents) ? parents[0].GetString() : null, p.GetProperty("properties").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!), content);
                    Entries.Add(id, entry); Creates.Add((entry.Name, content.ToArray())); Writes++; result = entry.Metadata();
                }
            }
            else
            {
                var id = uri.AbsolutePath.Split('/')[^1];
                if (!Entries.TryGetValue(id, out var entry)) { status = 404; result = new { error = "missing" }; }
                else if (request.Method == HttpMethod.Patch) { Patches++; Writes++; entry.Bytes = await request.Content!.ReadAsByteArrayAsync(ct); result = entry.Metadata(); }
                else if (query.GetValueOrDefault("alt") == "media") { result = new { }; raw = entry.Bytes; }
                else result = uri.AbsolutePath == "/drive/v2/files/" + id ? entry.MetadataV2() : entry.Metadata();
            }
            // Intentionally supplies neither a response ETag nor a v2 body ETag.
            raw ??= JsonSerializer.SerializeToUtf8Bytes(result); Assert.True(raw.Length <= maximumBytes);
            await receive(raw, ct); return status;
        }
        static async Task<(byte[] Metadata, byte[] Content)> CreationParts(HttpContent content, CancellationToken ct)
        {
            var bytes = await content.ReadAsByteArrayAsync(ct);
            if (content.Headers.ContentType?.MediaType != "multipart/related") return (bytes, []);
            var boundary = content.Headers.ContentType.Parameters.Single(x => x.Name == "boundary").Value!.Trim('"');
            // All fixture payloads are UTF-8 JSON; preserve the content bytes apart from MIME framing.
            var parts = Encoding.UTF8.GetString(bytes).Split("--" + boundary).Where(x => x.Contains("\r\n\r\n", StringComparison.Ordinal)).Select(x => x[(x.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..].TrimEnd('\r', '\n')).ToArray();
            Assert.Equal(2, parts.Length); return (Encoding.UTF8.GetBytes(parts[0]), Encoding.UTF8.GetBytes(parts[1]));
        }
    }
}
