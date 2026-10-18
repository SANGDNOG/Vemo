using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Vemo.Core;

public sealed class GoogleConnectionException(string code) : Exception("Google connection: " + code)
{
    public string Code { get; } = code;
}

public sealed class GoogleOAuthConfiguration
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public string ClientId { get; }
    internal string? ClientSecret { get; }
    GoogleOAuthConfiguration(string clientId, string? secret)
    {
        if (clientId.Length is <= 27 or > 1024 || !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || clientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || Encoding.UTF8.GetByteCount(secret ?? "") > 4096)
            throw new GoogleConnectionException("invalid_configuration");
        ClientId = clientId; ClientSecret = secret;
    }
    /// Only installed client ID/secret are read. Supplied endpoints and redirect URIs are ignored.
    public static GoogleOAuthConfiguration ParseInstalledJson(string json)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > 65536) throw new GoogleConnectionException("invalid_configuration");
            using var doc = JsonFormat.Strict(json); var installed = doc.RootElement.GetProperty("installed");
            return new(installed.GetProperty("client_id").GetString()!, installed.TryGetProperty("client_secret", out var secret) ? secret.GetString() : null);
        }
        catch (Exception e) when (e is not GoogleConnectionException) { throw new GoogleConnectionException("invalid_configuration"); }
    }
    internal string Serialize() => JsonSerializer.Serialize(new { installed = new { client_id = ClientId, client_secret = ClientSecret } });
}

public sealed class GoogleOAuthPkce
{
    public string Verifier { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));
    public string State { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));
    static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public Uri AuthorizationUri(GoogleOAuthConfiguration config, Uri redirect) => new(GoogleOAuthConfiguration.AuthorizationEndpoint + "?" + GoogleWire.Query(new Dictionary<string, string>
    {
        ["client_id"] = config.ClientId, ["redirect_uri"] = redirect.AbsoluteUri, ["response_type"] = "code", ["scope"] = GoogleOAuthConfiguration.Scope,
        ["state"] = State, ["code_challenge"] = ChallengeFor(Verifier), ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent"
    }));
    public string ParseCallback(string target)
    {
        if (target.Length > 8192 || !target.StartsWith("/oauth/callback?", StringComparison.Ordinal) || target.Contains('#')) throw new GoogleConnectionException("invalid_callback");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var part in target[16..].Split('&'))
            {
                var pair = part.Split('=', 2); var key = Uri.UnescapeDataString(pair[0].Replace('+', ' ')); var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
                if (!query.TryAdd(key, value)) throw new GoogleConnectionException("invalid_callback");
            }
        }
        catch (UriFormatException) { throw new GoogleConnectionException("invalid_callback"); }
        if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(state)), SHA256.HashData(Encoding.UTF8.GetBytes(State)))) throw new GoogleConnectionException("state_mismatch");
        if (query.ContainsKey("error") && !query.ContainsKey("code")) throw new GoogleConnectionException("access_denied");
        if (query.ContainsKey("error") || !query.TryGetValue("code", out var code) || code.Length is 0 or > 4096 || code.Any(char.IsControl)) throw new GoogleConnectionException("invalid_callback");
        return code;
    }
}

public sealed class GoogleOAuthClient : IDisposable
{
    readonly object sync = new(); readonly SemaphoreSlim gate = new(1, 1);
    readonly GoogleOAuthConfiguration config; readonly IGoogleCredentialVault vault; readonly IGoogleHttpTransport http;
    CancellationTokenSource session = new(); string? access; DateTimeOffset expiration; long generation; bool disposed;
    public GoogleOAuthClient(GoogleOAuthConfiguration configuration, IGoogleCredentialVault vault, IGoogleHttpTransport http) { config = configuration; this.vault = vault; this.http = http; }
    public bool Connected { get { lock (sync) return !disposed && vault.Load("refresh") != null; } }
    (long Generation, CancellationTokenSource Token) Begin(CancellationToken ct)
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); return (generation, CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token)); }
    }
    void Active(long epoch, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (epoch != generation || disposed) throw new OperationCanceledException(); }
    public async Task ConnectAsync(Func<Uri, Task> openBrowser, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var (epoch, operation) = Begin(ct); using (operation)
        {
            var duration = timeout ?? TimeSpan.FromMinutes(3); if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(10)) throw new GoogleConnectionException("invalid_timeout"); operation.CancelAfter(duration);
            if (!await gate.WaitAsync(0, operation.Token)) throw new GoogleConnectionException("busy");
            try
            {
                var pkce = new GoogleOAuthPkce(); using var receiver = new GoogleOAuthLoopback(pkce);
                var waiting = receiver.WaitForCodeAsync(operation.Token);
                try
                {
                    await openBrowser(pkce.AuthorizationUri(config, receiver.RedirectUri)).WaitAsync(operation.Token);
                    var code = await waiting;
                    var result = await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = receiver.RedirectUri.AbsoluteUri, ["code_verifier"] = pkce.Verifier }, operation.Token);
                    lock (sync) { Active(epoch, operation.Token); if (result.Refresh == null) throw new GoogleConnectionException("invalid_response"); vault.Save("refresh", result.Refresh); access = result.Access; expiration = DateTimeOffset.UtcNow.AddSeconds(result.Seconds); }
                }
                finally { operation.Cancel(); try { await waiting; } catch (Exception) { /* Observe the listener on every exit. */ } }
            }
            finally { gate.Release(); }
        }
    }
    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        var (epoch, operation) = Begin(ct); using (operation)
        {
            operation.CancelAfter(TimeSpan.FromSeconds(45)); await gate.WaitAsync(operation.Token);
            try
            {
                string refresh;
                lock (sync) { Active(epoch, operation.Token); if (access != null && expiration > DateTimeOffset.UtcNow.AddSeconds(60)) return access; refresh = vault.Load("refresh") ?? throw new GoogleConnectionException("not_connected"); }
                try
                {
                    var result = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, operation.Token);
                    lock (sync) { Active(epoch, operation.Token); if (result.Refresh != null) vault.Save("refresh", result.Refresh); access = result.Access; expiration = DateTimeOffset.UtcNow.AddSeconds(result.Seconds); return result.Access; }
                }
                catch (GoogleConnectionException e) when (e.Code == "reauthorization_required")
                {
                    lock (sync) { Active(epoch, operation.Token); access = null; expiration = default; vault.Delete("refresh"); } throw;
                }
            }
            finally { gate.Release(); }
        }
    }
    public void Disconnect()
    {
        CancellationTokenSource previous;
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); generation++; previous = session; session = new(); access = null; expiration = default; vault.Delete("refresh"); }
        previous.Cancel(); previous.Dispose();
    }
    public void Dispose()
    {
        CancellationTokenSource previous;
        lock (sync) { if (disposed) return; disposed = true; generation++; access = null; previous = session; }
        previous.Cancel(); previous.Dispose();
    }
    sealed record Token(string Access, string? Refresh, double Seconds);
    async Task<Token> ExchangeAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = config.ClientId; if (config.ClientSecret != null) fields["client_secret"] = config.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, GoogleOAuthConfiguration.TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
        var reply = await GoogleWire.BufferAsync(http, request, 65536, ct); ct.ThrowIfCancellationRequested();
        try
        {
            using var doc = GoogleWire.Strict(reply.Bytes); var p = doc.RootElement;
            if (reply.Status != 200)
            {
                if (p.TryGetProperty("error", out var error) && error.GetString() == "invalid_grant") throw new GoogleConnectionException("reauthorization_required");
                throw new GoogleConnectionException("token_request_failed");
            }
            var token = p.GetProperty("access_token").GetString()!; var seconds = p.GetProperty("expires_in").GetDouble();
            var refresh = p.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            if (!GoogleWire.ValidToken(token) || (r.ValueKind != JsonValueKind.Undefined && !GoogleWire.ValidToken(refresh)) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 604800 || !string.Equals(p.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new GoogleConnectionException("invalid_response");
            if (p.TryGetProperty("scope", out var scope) && !(scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet().SetEquals([GoogleOAuthConfiguration.Scope])) throw new GoogleConnectionException("invalid_scope");
            return new(token, refresh, seconds);
        }
        catch (Exception e) when (e is not (GoogleConnectionException or OperationCanceledException)) { throw new GoogleConnectionException("invalid_response"); }
    }
}
