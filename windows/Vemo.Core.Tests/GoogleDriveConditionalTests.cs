using System.Text.Json;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class GoogleDriveConditionalTests
{
    sealed class Backend : IGoogleHttpTransport
    {
        public byte[] Bytes = "{\"version\":1}"u8.ToArray();
        public string ETag = "\"version-1\"";
        public int Reads, Updates;
        public Func<Dictionary<string, object?>, Dictionary<string, object?>>? Metadata;
        public Action<HttpRequestMessage, CancellationToken>? Before;
        public bool OversizedMetadata;
        public bool CancelAfterReceive;
        public CancellationTokenSource? Cancellation;
        public async Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!;
            if (uri.Host == "oauth2.googleapis.com")
            {
                await receive("{\"access_token\":\"fixture-access\",\"token_type\":\"Bearer\",\"expires_in\":3600}"u8.ToArray(), ct); return 200;
            }
            Before?.Invoke(request, ct); ct.ThrowIfCancellationRequested();
            var status = 200; byte[] response;
            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/drive/v2/files/file-1")
            {
                Reads++;
                var fields = Uri.UnescapeDataString(uri.Query);
                foreach (var field in new[] { "id", "etag", "title", "mimeType", "parents", "properties", "labels", "fileSize" }) Assert.Contains(field, fields);
                var metadata = new Dictionary<string, object?>
                {
                    ["id"] = "file-1", ["etag"] = ETag, ["title"] = "result.json", ["mimeType"] = "application/json",
                    ["parents"] = new[] { new { id = "parent-1" } }, ["labels"] = new { trashed = false },
                    ["properties"] = new[] { new { key = "public-key", value = "public-value", visibility = "PUBLIC" }, new { key = "public-key", value = "private-value", visibility = "PRIVATE" } },
                    ["fileSize"] = Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                response = OversizedMetadata ? new byte[8 * 1024 * 1024 + 1] : JsonSerializer.SerializeToUtf8Bytes(Metadata?.Invoke(metadata) ?? metadata);
            }
            else if (request.Method == HttpMethod.Put && uri.AbsolutePath == "/upload/drive/v2/files/file-1")
            {
                Assert.Equal("?uploadType=media", uri.Query); Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                var match = Assert.Single(request.Headers.IfMatch).ToString();
                if (match != ETag) { status = 412; response = "{}"u8.ToArray(); }
                else { Bytes = await request.Content.ReadAsByteArrayAsync(ct); ETag = "\"version-2\""; Updates++; response = "{}"u8.ToArray(); }
            }
            else if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/drive/v3/files/file-1" && uri.Query == "?alt=media")
                response = Bytes; // Deliberately ignores If-Match, as v3 cannot protect this read.
            else throw new InvalidOperationException("Unexpected Drive endpoint: " + request.Method + " " + uri.AbsolutePath);
            if (uri.AbsolutePath.Contains("/v2/", StringComparison.Ordinal)) Assert.Equal(8 * 1024 * 1024, maximumBytes);
            await receive(response, ct);
            if (CancelAfterReceive) Cancellation!.Cancel();
            return status;
        }
    }

    static GoogleOAuthClient OAuth(IGoogleHttpTransport transport)
    {
        var vault = new GoogleMemoryVault(); vault.Save("refresh", "fixture-refresh");
        return new(GoogleOAuthConfiguration.ParseInstalledJson(GoogleOAuthTests.Configuration), vault, transport);
    }

    [Fact] public async Task MetadataAndBodyETagComeFromOneV2SnapshotWithoutResponseHeader()
    {
        var backend = new Backend(); using var oauth = OAuth(backend); var drive = new GoogleDriveClient(oauth, backend);
        var file = await drive.FileAsync("file-1");
        Assert.Equal("file-1", file.Id); Assert.Equal("result.json", file.Name); Assert.Equal("application/json", file.MimeType);
        Assert.Equal(new[] { "parent-1" }, file.Parents); Assert.False(file.Trashed); Assert.Equal((long)backend.Bytes.Length, file.Size);
        var property = Assert.Single(file.Properties); Assert.Equal("public-key", property.Key); Assert.Equal("public-value", property.Value);
        Assert.Equal("\"version-1\"", file.ETag); Assert.Equal(1, backend.Reads);
    }

    [Fact] public async Task MissingOptionalCollectionsAndFolderSizeMapWithoutInventingMetadata()
    {
        var backend = new Backend { Metadata = p => { p["mimeType"] = GoogleDriveClient.FolderMime; p["labels"] = new { trashed = true }; p.Remove("fileSize"); p.Remove("parents"); p.Remove("properties"); return p; } };
        using var oauth = OAuth(backend); var file = await new GoogleDriveClient(oauth, backend).FileAsync("file-1");
        Assert.True(file.Trashed); Assert.Null(file.Size); Assert.Empty(file.Parents); Assert.Empty(file.Properties);
    }

    [Fact] public async Task V2PutRejectsStaleSnapshotAndPreservesFirstReplacement()
    {
        var backend = new Backend(); using var oauth = OAuth(backend); var drive = new GoogleDriveClient(oauth, backend);
        var snapshot = await drive.FileAsync("file-1"); var replacement = "{\"version\":2}"u8.ToArray();
        await drive.UploadAsync(snapshot.Id, replacement, snapshot.ETag!);
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => drive.UploadAsync(snapshot.Id, "{\"version\":3}"u8.ToArray(), snapshot.ETag!));
        Assert.Equal("remote_conflict", error.Code); Assert.Equal(replacement, backend.Bytes); Assert.Equal(1, backend.Updates);
    }

    [Fact] public async Task IgnoredV3DownloadConditionCannotAuthorizeAStaleWrite()
    {
        var backend = new Backend(); using var oauth = OAuth(backend); var drive = new GoogleDriveClient(oauth, backend);
        var snapshot = await drive.FileAsync("file-1"); var foreign = "{\"version\":9}"u8.ToArray();
        backend.Bytes = foreign; backend.ETag = "\"foreign-version\"";
        Assert.Equal(foreign, await drive.JsonAsync(snapshot));
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => drive.UploadAsync(snapshot.Id, "{}"u8.ToArray(), snapshot.ETag!));
        Assert.Equal("remote_conflict", error.Code); Assert.Equal(foreign, backend.Bytes); Assert.Equal(0, backend.Updates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("W/\"weak\"")]
    public async Task MissingOrWeakBodyETagCannotAuthorizeAnUpdate(string? etag)
    {
        var backend = new Backend { Metadata = p => { if (etag == null) p.Remove("etag"); else p["etag"] = etag; return p; } };
        using var oauth = OAuth(backend); var drive = new GoogleDriveClient(oauth, backend); var original = backend.Bytes.ToArray();
        var file = await drive.FileAsync("file-1");
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => drive.UploadAsync(file.Id, "{}"u8.ToArray(), file.ETag ?? ""));
        Assert.Equal("conditional_update_unavailable", error.Code); Assert.Equal(original, backend.Bytes); Assert.Equal(0, backend.Updates);
    }

    [Theory]
    [InlineData("wrong-id", "identity_mismatch")]
    [InlineData("duplicate-public", "invalid_metadata")]
    [InlineData("duplicate-private", "invalid_metadata")]
    [InlineData("unknown-visibility", "invalid_metadata")]
    [InlineData("missing-visibility", "invalid_metadata")]
    [InlineData("null-value", "invalid_metadata")]
    [InlineData("empty-key", "invalid_metadata")]
    [InlineData("duplicate-parent", "invalid_metadata")]
    [InlineData("negative-size", "invalid_size")]
    [InlineData("overflow-size", "invalid_size")]
    [InlineData("numeric-size", "invalid_size")]
    [InlineData("signed-size", "invalid_size")]
    public async Task AmbiguousOrInvalidV2MetadataNeverReturnsAnUpdateSnapshot(string mutation, string code)
    {
        var backend = new Backend { Metadata = p =>
        {
            switch (mutation)
            {
                case "wrong-id": p["id"] = "other-file"; break;
                case "duplicate-public": p["properties"] = new[] { new { key = "owner", value = "mine", visibility = "PUBLIC" }, new { key = "owner", value = "foreign", visibility = "PUBLIC" } }; break;
                case "duplicate-private": p["properties"] = new[] { new { key = "owner", value = "mine", visibility = "PRIVATE" }, new { key = "owner", value = "foreign", visibility = "PRIVATE" } }; break;
                case "unknown-visibility": p["properties"] = new[] { new { key = "owner", value = "mine", visibility = "OTHER" } }; break;
                case "missing-visibility": p["properties"] = new[] { new { key = "owner", value = "mine" } }; break;
                case "null-value": p["properties"] = new[] { new { key = "owner", value = (string?)null, visibility = "PUBLIC" } }; break;
                case "empty-key": p["properties"] = new[] { new { key = "", value = "mine", visibility = "PUBLIC" } }; break;
                case "duplicate-parent": p["parents"] = new[] { new { id = "parent-1" }, new { id = "parent-1" } }; break;
                case "negative-size": p["fileSize"] = "-1"; break;
                case "overflow-size": p["fileSize"] = "9223372036854775808"; break;
                case "numeric-size": p["fileSize"] = 12; break;
                case "signed-size": p["fileSize"] = "+12"; break;
            }
            return p;
        } };
        using var oauth = OAuth(backend); var drive = new GoogleDriveClient(oauth, backend);
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => drive.FileAsync("file-1"));
        Assert.Equal(code, error.Code); Assert.Equal(0, backend.Updates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOnV2MetadataOrUploadCannotPublish(bool upload)
    {
        var backend = new Backend(); using var oauth = OAuth(backend); var drive = new GoogleDriveClient(oauth, backend);
        var snapshot = await drive.FileAsync("file-1"); var original = backend.Bytes.ToArray(); using var cancellation = new CancellationTokenSource();
        backend.Before = (request, _) => { if (request.Method == (upload ? HttpMethod.Put : HttpMethod.Get)) cancellation.Cancel(); };
        if (upload) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drive.UploadAsync(snapshot.Id, "{}"u8.ToArray(), snapshot.ETag!, cancellation.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drive.FileAsync(snapshot.Id, cancellation.Token));
        Assert.Equal(original, backend.Bytes); Assert.Equal(0, backend.Updates);
    }

    [Fact] public async Task CancellationAfterMetadataReceiveDoesNotReturnSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend { CancelAfterReceive = true, Cancellation = cancellation }; using var oauth = OAuth(backend);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GoogleDriveClient(oauth, backend).FileAsync("file-1", cancellation.Token));
        Assert.Equal(0, backend.Updates);
    }

    [Fact] public async Task OversizedV2MetadataIsRejectedWhileReceiving()
    {
        var backend = new Backend { OversizedMetadata = true }; using var oauth = OAuth(backend);
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => new GoogleDriveClient(oauth, backend).FileAsync("file-1"));
        Assert.Equal("response_limit", error.Code); Assert.Equal(0, backend.Updates);
    }
}
