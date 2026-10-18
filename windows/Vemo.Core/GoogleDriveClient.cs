using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Vemo.Core;

public sealed record GoogleDriveFile(string Id, string Name, string MimeType, string[] Parents, Dictionary<string, string> Properties, bool Trashed, long? Size)
{
    public string? ETag { get; init; }
}

public sealed class GoogleDriveClient(GoogleOAuthClient oauth, IGoogleHttpTransport transport)
{
    public const string FolderMime = "application/vnd.google-apps.folder";
    const string Fields = "id,name,mimeType,parents,properties,trashed,size";
    public static string Identifier(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,256}$") ? value : throw new GoogleConnectionException("invalid_identity");
    public static string PathKey(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    public static Dictionary<string, string> PropertiesFor(string path) => new() { ["vemoProtocol"] = "1", ["vemoKey"] = PathKey(path) };
    static GoogleDriveFile Parse(JsonElement p)
    {
        var id = Identifier(p.GetProperty("id").GetString()!);
        return new(id, p.GetProperty("name").GetString()!, p.GetProperty("mimeType").GetString()!,
            p.TryGetProperty("parents", out var parents) ? parents.EnumerateArray().Select(x => Identifier(x.GetString()!)).ToArray() : [],
            p.TryGetProperty("properties", out var props) ? props.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!) : [],
            p.GetProperty("trashed").GetBoolean(), p.TryGetProperty("size", out var size) ? long.Parse(size.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null);
    }
    static GoogleDriveFile ParseV2(JsonElement p)
    {
        static string Text(JsonElement item, string key)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) throw new GoogleConnectionException("invalid_metadata");
            return value.GetString()!;
        }
        var id = Identifier(Text(p, "id")); var name = Text(p, "title"); var mime = Text(p, "mimeType");
        var parents = new List<string>(); var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p.TryGetProperty("parents", out var parentItems))
        {
            if (parentItems.ValueKind != JsonValueKind.Array) throw new GoogleConnectionException("invalid_metadata");
            foreach (var item in parentItems.EnumerateArray())
            {
                var parent = Identifier(Text(item, "id")); if (parents.Contains(parent)) throw new GoogleConnectionException("invalid_metadata"); parents.Add(parent);
            }
        }
        if (p.TryGetProperty("properties", out var propertyItems))
        {
            if (propertyItems.ValueKind != JsonValueKind.Array) throw new GoogleConnectionException("invalid_metadata");
            var keys = new HashSet<(string Key, string Visibility)>();
            foreach (var item in propertyItems.EnumerateArray())
            {
                var key = Text(item, "key"); var value = Text(item, "value"); var visibility = Text(item, "visibility");
                if (key.Length == 0 || visibility is not ("PUBLIC" or "PRIVATE") || !keys.Add((key, visibility))) throw new GoogleConnectionException("invalid_metadata");
                // v3 properties exposes only PUBLIC entries; PRIVATE entries belong to appProperties.
                if (visibility == "PUBLIC") properties.Add(key, value);
            }
        }
        if (!p.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Object || !labels.TryGetProperty("trashed", out var trashed) || trashed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new GoogleConnectionException("invalid_metadata");
        long? size = null;
        if (p.TryGetProperty("fileSize", out var fileSize))
        {
            if (fileSize.ValueKind != JsonValueKind.String || !long.TryParse(fileSize.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)) throw new GoogleConnectionException("invalid_size");
            size = length;
        }
        var etag = p.TryGetProperty("etag", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
        return new(id, name, mime, parents.ToArray(), properties, trashed.GetBoolean(), size) { ETag = GoogleWire.StrongETag(etag) ? etag : null };
    }
    async Task<byte[]> Call(HttpMethod method, string path, Dictionary<string, string>? query, byte[]? body, CancellationToken ct, string? ifMatch = null)
    {
        using var request = await Request(method, path, query, ct);
        if (ifMatch != null)
        {
            if (!GoogleWire.StrongETag(ifMatch)) throw new GoogleConnectionException("conditional_update_unavailable");
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }
        if (body != null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        var reply = await GoogleWire.BufferAsync(transport, request, GoogleWire.JsonLimit, ct);
        if (reply.Status == 412) throw new GoogleConnectionException("remote_conflict");
        if (reply.Status == 404) throw new GoogleConnectionException("not_found");
        if (reply.Status is 401 or 403) throw new GoogleConnectionException("reauthorization_required");
        if (reply.Status is not (200 or 201)) throw new GoogleConnectionException("drive_request_failed");
        return reply.Bytes;
    }
    async Task<HttpRequestMessage> Request(HttpMethod method, string path, Dictionary<string, string>? query, CancellationToken ct)
    {
        var token = await oauth.AccessTokenAsync(ct); ct.ThrowIfCancellationRequested();
        var request = new HttpRequestMessage(method, "https://www.googleapis.com" + path + (query?.Count > 0 ? "?" + GoogleWire.Query(query) : ""));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return request;
    }
    public async Task<string> AccountIdAsync(CancellationToken ct = default)
    {
        using var doc = GoogleWire.Strict(await Call(HttpMethod.Get, "/drive/v3/about", new() { ["fields"] = "user(permissionId)" }, null, ct));
        return Identifier(doc.RootElement.GetProperty("user").GetProperty("permissionId").GetString()!);
    }
    public async Task<GoogleDriveFile> FileAsync(string id, CancellationToken ct = default)
    {
        // All identity metadata and the update precondition must describe the same v2 snapshot.
        using var doc = GoogleWire.Strict(await Call(HttpMethod.Get, "/drive/v2/files/" + Identifier(id), new() { ["fields"] = GoogleWire.DriveMetadataFieldsV2 }, null, ct));
        var file = ParseV2(doc.RootElement); if (file.Id != id) throw new GoogleConnectionException("identity_mismatch"); return file;
    }
    public async Task<IReadOnlyList<GoogleDriveFile>> ListAsync(string query, CancellationToken ct = default)
    {
        var files = new List<GoogleDriveFile>(); var ids = new HashSet<string>(); var pages = new HashSet<string>(); string? page = null;
        for (int n = 0; n < 100; n++)
        {
            var fields = new Dictionary<string, string> { ["q"] = query, ["spaces"] = "drive", ["pageSize"] = "100", ["fields"] = "nextPageToken,incompleteSearch,files(" + Fields + ")" }; if (page != null) fields["pageToken"] = page;
            using var doc = GoogleWire.Strict(await Call(HttpMethod.Get, "/drive/v3/files", fields, null, ct)); var p = doc.RootElement;
            if (p.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean()) throw new GoogleConnectionException("incomplete_search");
            foreach (var item in p.GetProperty("files").EnumerateArray()) { var file = Parse(item); if (!ids.Add(file.Id)) throw new GoogleConnectionException("duplicate_drive_identity"); files.Add(file); }
            if (!p.TryGetProperty("nextPageToken", out var next)) return files;
            page = next.GetString(); if (string.IsNullOrEmpty(page) || page.Length > 4096 || !pages.Add(page)) throw new GoogleConnectionException("invalid_pagination");
        }
        throw new GoogleConnectionException("listing_limit");
    }
    public Task<IReadOnlyList<GoogleDriveFile>> ChildrenAsync(string parent, CancellationToken ct = default) => ListAsync("'" + Identifier(parent) + "' in parents and trashed = false", ct);
    public static GoogleDriveFolder Root(GoogleDriveFile file)
    {
        if (file.Trashed || file.MimeType != FolderMime || string.IsNullOrWhiteSpace(file.Name) || file.Properties.Count != 3 || file.Properties.GetValueOrDefault("vemoProtocol") != "1" || file.Properties.GetValueOrDefault("vemoKind") != "root" || !Guid.TryParseExact(file.Properties.GetValueOrDefault("vemoProcessor"), "D", out var id) || id.ToString() != file.Properties["vemoProcessor"]) throw new GoogleConnectionException("invalid_root");
        return new(file.Id, file.Name, id.ToString());
    }
    public async Task<IReadOnlyList<GoogleDriveFolder>> RootsAsync(CancellationToken ct = default)
    {
        var roots = (await ListAsync("trashed = false and properties has { key='vemoKind' and value='root' }", ct)).Select(Root).ToArray();
        if (roots.GroupBy(x => x.Name, StringComparer.Ordinal).Any(x => x.Count() > 1) || roots.GroupBy(x => x.ProcessorId).Any(x => x.Count() > 1)) throw new GoogleConnectionException("duplicate_roots"); return roots;
    }
    public static void Validate(GoogleDriveFile file, string parent, string path, string mime)
    {
        var expected = PropertiesFor(path);
        if (file.Trashed || file.Name != path.Split('/')[^1] || file.MimeType != mime || file.Parents.Length != 1 || file.Parents[0] != parent || file.Properties.Count != 2 || expected.Any(x => file.Properties.GetValueOrDefault(x.Key) != x.Value)) throw new GoogleConnectionException("invalid_metadata");
    }
    public async Task<GoogleDriveFile?> ChildAsync(string parent, string path, string mime, CancellationToken ct = default)
    {
        var candidates = (await ChildrenAsync(parent, ct)).Where(x => x.Name == path.Split('/')[^1] || x.Properties.GetValueOrDefault("vemoKey") == PathKey(path)).ToArray();
        if (candidates.Length > 1) throw new GoogleConnectionException("duplicate_path");
        if (candidates.Length == 0) return null; Validate(candidates[0], parent, path, mime); return candidates[0];
    }
    public async Task<string> AllocateIdAsync(CancellationToken ct = default)
    {
        using var doc = GoogleWire.Strict(await Call(HttpMethod.Get, "/drive/v3/files/generateIds", new() { ["count"] = "1", ["space"] = "drive", ["type"] = "files" }, null, ct));
        var ids = doc.RootElement.GetProperty("ids"); if (ids.GetArrayLength() != 1) throw new GoogleConnectionException("invalid_response"); return Identifier(ids[0].GetString()!);
    }
    public async Task<GoogleDriveFile> CreateAsync(string id, string name, string? parent, string mime, Dictionary<string, string> properties, CancellationToken ct = default)
    {
        var obj = new Dictionary<string, object> { ["id"] = Identifier(id), ["name"] = name, ["mimeType"] = mime, ["properties"] = properties }; if (parent != null) obj["parents"] = new[] { Identifier(parent) };
        using var doc = GoogleWire.Strict(await Call(HttpMethod.Post, "/drive/v3/files", new() { ["fields"] = Fields }, JsonSerializer.SerializeToUtf8Bytes(obj), ct)); var file = Parse(doc.RootElement);
        if (file.Id != id) throw new GoogleConnectionException("identity_mismatch"); return file;
    }
    /// One create request commits metadata and JSON content under a durably reserved ID.
    /// Duplicate IDs never update content; an unknown response is resolved by identity and exact readback.
    public async Task<GoogleDriveFile> CreateJsonAsync(string id, string parent, string path, byte[] bytes, CancellationToken ct = default)
    {
        Identifier(id); Identifier(parent);
        if (bytes.Length is <= 0 or > GoogleWire.JsonLimit) throw new GoogleConnectionException("response_limit");
        using var request = await Request(HttpMethod.Post, "/upload/drive/v3/files", new() { ["uploadType"] = "multipart", ["fields"] = Fields }, ct);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { id, name = path.Split('/')[^1], parents = new[] { parent }, mimeType = "application/json", properties = PropertiesFor(path) });
        var multipart = new MultipartContent("related");
        var meta = new ByteArrayContent(metadata); meta.Headers.ContentType = new MediaTypeHeaderValue("application/json"); multipart.Add(meta);
        var media = new ByteArrayContent(bytes); media.Headers.ContentType = new MediaTypeHeaderValue("application/json"); multipart.Add(media); request.Content = multipart;
        var reply = await GoogleWire.BufferAsync(transport, request, GoogleWire.JsonLimit, ct);
        if (reply.Status is 401 or 403) throw new GoogleConnectionException("reauthorization_required");
        if (reply.Status is not (200 or 201 or 409)) throw new GoogleConnectionException("drive_request_failed");
        var confirmed = await FileAsync(id, ct); Validate(confirmed, parent, path, "application/json");
        if (!(await JsonAsync(confirmed, ct)).AsSpan().SequenceEqual(bytes)) throw new GoogleConnectionException("result_conflict");
        return confirmed;
    }
    public async Task UploadAsync(string id, byte[] bytes, string expectedETag, CancellationToken ct = default)
    {
        if (!GoogleWire.StrongETag(expectedETag)) throw new GoogleConnectionException("conditional_update_unavailable");
        if (bytes.Length > GoogleWire.JsonLimit) throw new GoogleConnectionException("response_limit");
        // v3 media PATCH can ignore If-Match; v2 PUT enforces the v2 metadata ETag.
        _ = await Call(HttpMethod.Put, "/upload/drive/v2/files/" + Identifier(id), new() { ["uploadType"] = "media" }, bytes, ct, expectedETag);
    }
    public async Task DownloadAsync(string id, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct = default, string? ifMatch = null)
    {
        using var request = await Request(HttpMethod.Get, "/drive/v3/files/" + Identifier(id), new() { ["alt"] = "media" }, ct);
        if (ifMatch != null)
        {
            // Best effort only: v3 media GET may ignore this condition. Callers verify content/identity.
            if (!GoogleWire.StrongETag(ifMatch)) throw new GoogleConnectionException("conditional_update_unavailable");
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }
        long count = 0; var status = await transport.SendAsync(request, maximumBytes, async (block, token) => { count += block.Length; if (count > maximumBytes) throw new GoogleConnectionException("response_limit"); await receive(block, token); }, ct);
        ct.ThrowIfCancellationRequested(); if (status == 412) throw new GoogleConnectionException("remote_conflict"); if (status != 200) throw new GoogleConnectionException("download_failed");
    }
    public async Task<byte[]> JsonAsync(GoogleDriveFile file, CancellationToken ct = default)
    {
        if (file.Size is not (> 0 and <= GoogleWire.JsonLimit)) throw new GoogleConnectionException("invalid_size"); using var buffer = new MemoryStream();
        await DownloadAsync(file.Id, file.Size.Value, (block, token) => buffer.WriteAsync(block, token), ct, file.ETag);
        if (buffer.Length != file.Size) throw new GoogleConnectionException("invalid_size"); return buffer.ToArray();
    }
}
