using System.Net;
using System.Text;
using System.Text.Json;
namespace Vemo.Core;

/// Implementations must reject redirects and bound bytes while receiving, including chunked bodies.
public interface IGoogleHttpTransport
{
    Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct);
}
public sealed class GoogleHttpTransport : IGoogleHttpTransport, IDisposable
{
    readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = Timeout.InfiniteTimeSpan };
    static bool V2Endpoint(HttpRequestMessage request, Uri uri)
    {
        var prefix = request.Method == HttpMethod.Get ? "/drive/v2/files/" : request.Method == HttpMethod.Put ? "/upload/drive/v2/files/" : null;
        if (prefix == null || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = uri.AbsolutePath[prefix.Length..];
        if (id.Length is < 1 or > 256 || !id.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return false;
        return uri.Query == (request.Method == HttpMethod.Get ? "?fields=" + Uri.EscapeDataString(GoogleWire.DriveMetadataFieldsV2) : "?uploadType=media");
    }
    public async Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
    {
        var u = request.RequestUri ?? throw new GoogleConnectionException("invalid_endpoint");
        if (maximumBytes is <= 0 or > 2147483648 || u.Scheme != "https" || u.Port != 443 || u.UserInfo != "" || u.Fragment != "" ||
            !(u.Host == "oauth2.googleapis.com" && u.AbsolutePath == "/token" && u.Query == "" && request.Method == HttpMethod.Post ||
              u.Host == "www.googleapis.com" && (V2Endpoint(request, u) || u.AbsolutePath.StartsWith("/drive/v3/", StringComparison.Ordinal) && request.Method is var m && (m == HttpMethod.Get || m == HttpMethod.Post) || u.AbsolutePath == "/upload/drive/v3/files" && request.Method == HttpMethod.Post)))
            throw new GoogleConnectionException("invalid_endpoint");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(maximumBytes > GoogleWire.JsonLimit ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(60));
        try
        {
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 || response.Headers.Location != null || response.Content.Headers.ContentLocation != null || response.Content.Headers.ContentEncoding.Count != 0) throw new GoogleConnectionException("redirect_or_encoding_rejected");
            if (response.Content.Headers.ContentLength > maximumBytes) throw new GoogleConnectionException("response_limit");
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token); var buffer = new byte[65536]; long count = 0;
            while (true) { var n = await body.ReadAsync(buffer, timeout.Token); if (n == 0) break; count += n; if (count > maximumBytes) throw new GoogleConnectionException("response_limit"); await receive(buffer.AsMemory(0, n), timeout.Token); }
            ct.ThrowIfCancellationRequested(); return (int)response.StatusCode;
        }
        catch (HttpRequestException) { throw new GoogleConnectionException("transport_failed"); }
    }
    public void Dispose() => client.Dispose();
}
internal static class GoogleWire
{
    internal const string DriveMetadataFieldsV2 = "id,etag,title,mimeType,parents(id),properties(key,value,visibility),labels(trashed),fileSize";
    internal static bool StrongETag(string? value) => value is { Length: >= 2 and <= 512 } && value[0] == '"' && value[^1] == '"' && value.AsSpan(1, value.Length - 2).ToArray().All(c => c is >= '!' and <= '~' && c != '"');
    internal const int JsonLimit = 8 * 1024 * 1024;
    internal static string Query(IEnumerable<KeyValuePair<string, string>> fields) => string.Join("&", fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    internal static bool ValidToken(string? token) => token is { Length: > 0 and <= 16384 } && token.All(c => c is >= '!' and <= '~');
    internal static JsonDocument Strict(byte[] bytes)
    {
        if (bytes.Length > JsonLimit) throw new GoogleConnectionException("response_limit");
        var doc = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes), new JsonDocumentOptions { MaxDepth = 24 });
        try { Check(doc.RootElement); return doc; } catch { doc.Dispose(); throw; }
        static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object) { var keys = new HashSet<string>(StringComparer.Ordinal); foreach (var p in element.EnumerateObject()) { if (!keys.Add(p.Name)) throw new GoogleConnectionException("duplicate_json_key"); Check(p.Value); } }
            if (element.ValueKind == JsonValueKind.Array) foreach (var v in element.EnumerateArray()) Check(v);
        }
    }
    internal static async Task<(int Status, byte[] Bytes)> BufferAsync(IGoogleHttpTransport transport, HttpRequestMessage request, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var status = await transport.SendAsync(request, limit, (block, token) => { token.ThrowIfCancellationRequested(); if (buffer.Length + block.Length > limit) throw new GoogleConnectionException("response_limit"); buffer.Write(block.Span); return ValueTask.CompletedTask; }, ct);
        ct.ThrowIfCancellationRequested(); return (status, buffer.ToArray());
    }
    internal static void Keys(JsonElement p, params string[] keys)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(keys)) throw new GoogleConnectionException("invalid_protocol");
    }
}
