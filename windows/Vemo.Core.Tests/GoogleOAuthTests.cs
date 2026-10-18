using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class GoogleOAuthTests
{
    internal const string Configuration = "{\"installed\":{\"client_id\":\"fixture.apps.googleusercontent.com\",\"client_secret\":\"fixture-secret\",\"token_uri\":\"https://evil.example/token\",\"auth_uri\":\"https://evil.example/auth\"}}";
    [Fact] public void InstalledConfigurationIgnoresEndpointsAndRejectsDuplicateKeys()
    {
        var config = GoogleOAuthConfiguration.ParseInstalledJson(Configuration);
        Assert.Equal("fixture.apps.googleusercontent.com", config.ClientId);
        Assert.ThrowsAny<Exception>(() => GoogleOAuthConfiguration.ParseInstalledJson(Configuration.Replace("\"client_id\":", "\"client_id\":\"other.apps.googleusercontent.com\",\"client_id\":")));
        Assert.ThrowsAny<Exception>(() => GoogleOAuthConfiguration.ParseInstalledJson("{\"web\":{\"client_id\":\"fixture.apps.googleusercontent.com\"}}"));
        Assert.ThrowsAny<Exception>(() => GoogleOAuthConfiguration.ParseInstalledJson(new string('x', 65537)));
    }
    [Fact] public void PkceUsesS256AndRejectsAmbiguousOrWrongState()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleOAuthPkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var p = new GoogleOAuthPkce();
        Assert.Equal("code", p.ParseCallback("/oauth/callback?state=" + p.State + "&code=code"));
        Assert.ThrowsAny<Exception>(() => p.ParseCallback("/oauth/callback?state=wrong&code=secret"));
        Assert.ThrowsAny<Exception>(() => p.ParseCallback("/oauth/callback?state=" + p.State + "&state=" + p.State + "&code=x"));
        Assert.ThrowsAny<Exception>(() => p.ParseCallback("/oauth/callback?state=" + p.State + "&code=x&code=y"));
        Assert.ThrowsAny<Exception>(() => p.ParseCallback("/oauth/callback?state=" + p.State + "&error=access_denied"));
    }
    [Fact] public async Task RealLoopbackUsesEphemeralIpv4AndRejectsForeignHost()
    {
        var pkce = new GoogleOAuthPkce(); using var receiver = new GoogleOAuthLoopback(pkce);
        Assert.Equal("127.0.0.1", receiver.RedirectUri.Host); Assert.True(receiver.RedirectUri.Port > 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); var waiting = receiver.WaitForCodeAsync(timeout.Token);
        async Task Send(string host, string state)
        {
            using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, receiver.RedirectUri.Port, timeout.Token);
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /oauth/callback?state={state}&code=fixture-code HTTP/1.1\r\nHost: {host}\r\n\r\n"), timeout.Token);
            var buffer = new byte[1]; await socket.GetStream().ReadExactlyAsync(buffer, timeout.Token);
        }
        await Send("evil.example", pkce.State); Assert.False(waiting.IsCompleted);
        await Send(receiver.RedirectUri.Authority, pkce.State); Assert.Equal("fixture-code", await waiting);
    }
    [Fact] public async Task RefreshInvalidGrantDeletesTokenAndDoesNotLeakResponse()
    {
        var vault = new GoogleMemoryVault(); vault.Save("refresh", "refresh-fixture");
        var http = new GoogleFixtureTransport((_, _) => Task.FromResult((400, Encoding.UTF8.GetBytes("{\"error\":\"invalid_grant\",\"error_description\":\"private server text\"}"))));
        var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(Configuration), vault, http);
        var error = await Assert.ThrowsAsync<GoogleConnectionException>(() => oauth.AccessTokenAsync());
        Assert.Equal("reauthorization_required", error.Code); Assert.Null(vault.Load("refresh")); Assert.DoesNotContain("private", error.Message);
    }
    [Fact] public async Task DisconnectDuringRefreshCannotResurrectCredential()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new GoogleMemoryVault(); vault.Save("refresh", "old-fixture");
        var http = new GoogleFixtureTransport(async (_, _) => { started.SetResult(); await finish.Task; return (200, Encoding.UTF8.GetBytes("{\"access_token\":\"fixture\",\"refresh_token\":\"replacement\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")); });
        var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(Configuration), vault, http);
        var pending = oauth.AccessTokenAsync(); await started.Task; oauth.Disconnect(); finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Null(vault.Load("refresh"));
    }
    [Fact] public async Task TokenScopeInjectionAndOversizeAreRejected()
    {
        foreach (var body in new[] { "{\"access_token\":\"bad\\r\\nheader\",\"expires_in\":3600,\"token_type\":\"Bearer\"}", "{\"access_token\":\"fixture\",\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"https://www.googleapis.com/auth/drive\"}", new string('x', 65537) })
        {
            var vault = new GoogleMemoryVault(); vault.Save("refresh", "fixture");
            var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(Configuration), vault, new GoogleFixtureTransport((_, _) => Task.FromResult((200, Encoding.UTF8.GetBytes(body)))));
            await Assert.ThrowsAnyAsync<Exception>(() => oauth.AccessTokenAsync());
        }
    }
    static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
    static async Task Callback(Uri authorization, bool deny = false)
    {
        var query = Query(authorization); var redirect = new Uri(query["redirect_uri"]);
        using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, redirect.Port);
        await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /oauth/callback?state={query["state"]}&{(deny ? "error=access_denied" : "code=fixture-code")} HTTP/1.1\r\nHost: {redirect.Authority}\r\n\r\n"));
    }
    [Fact] public async Task ConnectDenialAndCallerCancellationNeverStoreCredential()
    {
        var vault = new GoogleMemoryVault(); int exchanges = 0;
        var http = new GoogleFixtureTransport((_, _) => { exchanges++; throw new InvalidOperationException("No exchange expected"); });
        using var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(Configuration), vault, http);
        var denied = await Assert.ThrowsAsync<GoogleConnectionException>(() => oauth.ConnectAsync(uri => Callback(uri, true)));
        Assert.Equal("access_denied", denied.Code); Assert.Null(vault.Load("refresh"));
        using var ct = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oauth.ConnectAsync(_ => { ct.Cancel(); return Task.CompletedTask; }, ct.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oauth.ConnectAsync(_ => Task.CompletedTask, timeout: TimeSpan.FromMilliseconds(20)));
        Assert.Equal(0, exchanges); Assert.Null(vault.Load("refresh"));
    }
    [Fact] public async Task ConnectUsesPkceAndConcurrentRefreshIsSingleFlight()
    {
        var vault = new GoogleMemoryVault(); int exchanges = 0;
        var http = new GoogleFixtureTransport(async (request, ct) =>
        {
            exchanges++; var body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Equal(GoogleOAuthConfiguration.TokenEndpoint, request.RequestUri!.AbsoluteUri);
            Assert.Contains("code_verifier=", body); Assert.Contains("grant_type=authorization_code", body);
            return (200, Encoding.UTF8.GetBytes("{\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));
        });
        using var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(Configuration), vault, http);
        await oauth.ConnectAsync(async uri => { var q = Query(uri); Assert.Equal("S256", q["code_challenge_method"]); Assert.Equal(GoogleOAuthConfiguration.Scope, q["scope"]); Assert.Equal("accounts.google.com", uri.Host); await Callback(uri); });
        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => oauth.AccessTokenAsync()));
        Assert.All(tokens, token => Assert.Equal("fixture-access", token)); Assert.Equal(1, exchanges); Assert.Equal("fixture-refresh", vault.Load("refresh"));
    }
    [Fact] public async Task DisconnectThenReconnectIgnoresLateRefreshResponse()
    {
        var vault = new GoogleMemoryVault(); vault.Save("refresh", "old-refresh");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
        var http = new GoogleFixtureTransport(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.SetResult(); await finish.Task; return (200, Encoding.UTF8.GetBytes("{\"access_token\":\"old-access\",\"refresh_token\":\"stale-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}")); }
            return (200, Encoding.UTF8.GetBytes("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));
        });
        using var oauth = new GoogleOAuthClient(GoogleOAuthConfiguration.ParseInstalledJson(Configuration), vault, http);
        var stale = oauth.AccessTokenAsync(); await started.Task; oauth.Disconnect(); finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
        await oauth.ConnectAsync(uri => Callback(uri)); Assert.Equal("new-refresh", vault.Load("refresh")); Assert.Equal("new-access", await oauth.AccessTokenAsync());
    }
    [Fact] public async Task ProductionTransportRejectsNonGoogleAndWrongPathsBeforeNetwork()
    {
        using var transport = new GoogleHttpTransport();
        foreach (var url in new[] { "http://www.googleapis.com/drive/v3/files", "https://evil.example/token", "https://www.googleapis.com.evil.example/drive/v3/files", "https://www.googleapis.com/evil", "https://oauth2.googleapis.com/other" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync(request, 100, (_, _) => ValueTask.CompletedTask, default));
        }
    }
}

internal sealed class GoogleMemoryVault : IGoogleCredentialVault
{
    readonly Dictionary<string, string> values = [];
    public string? Load(string key) { lock (values) return values.GetValueOrDefault(key); }
    public void Save(string key, string value) { lock (values) values[key] = value; }
    public void Delete(string key) { lock (values) values.Remove(key); }
}
internal sealed class GoogleFixtureTransport(Func<HttpRequestMessage, CancellationToken, Task<(int Status, byte[] Body)>> respond) : IGoogleHttpTransport
{
    public async Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
    {
        var result = await respond(request, ct); await receive(result.Body, ct); return result.Status;
    }
}
