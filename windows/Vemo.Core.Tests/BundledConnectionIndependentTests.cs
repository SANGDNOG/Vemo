using System.Text.Json;
using Vemo.Core;
using Xunit;

namespace Vemo.Core.Tests;

public sealed class BundledConnectionIndependentTests
{
    const string Bundle = "{\"installed\":{\"client_id\":\"bundled-fixture.apps.googleusercontent.com\"}}";
    const string Stored = "{\"installed\":{\"client_id\":\"stored-fixture.apps.googleusercontent.com\"}}";

    [Fact]
    public void Fresh_bundle_configures_without_connecting_writing_credentials_or_network()
    {
        using var f = new Fixture(); var reads = 0;
        using var service = new GoogleConnectionService(f.Store, f.Vault, f.Http, () => { reads++; return Bundle; });
        Assert.True(service.Status.Configured); Assert.False(service.Status.Connected);
        Assert.False(service.Status.Busy); Assert.Null(service.Status.LastError);
        Assert.Null(service.Status.FolderId); Assert.Null(service.Status.LastSync);
        Assert.Equal(1, reads); Assert.Empty(f.Vault.Mutations); Assert.Equal(0, f.Http.Requests);
        Assert.Null(f.Vault.Load("configuration")); Assert.Null(f.Vault.Load("refresh"));
    }

    [Fact]
    public async Task Explicit_connect_uses_bundle_and_cancel_does_not_fake_account_connection()
    {
        using var f = new Fixture();
        using var service = new GoogleConnectionService(f.Store, f.Vault, f.Http, () => Bundle);
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        Assert.False(opened.Task.IsCompleted); Assert.Equal(0, f.Http.Requests);
        var operation = service.ConnectAsync(uri => { opened.SetResult(uri); return Task.CompletedTask; }, cancel.Token);
        var uri = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("accounts.google.com", uri.Host);
        Assert.Equal("bundled-fixture.apps.googleusercontent.com", Query(uri, "client_id"));
        Assert.Equal(GoogleOAuthConfiguration.Scope, Query(uri, "scope"));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(service.Status.Connected); Assert.False(service.Status.Busy);
        Assert.Equal("cancelled", service.Status.LastError); Assert.Empty(f.Vault.Mutations);
        Assert.Equal(0, f.Http.Requests);
    }

    [Fact]
    public async Task Stored_configuration_wins_without_reading_bundle_or_replacing_existing_token()
    {
        using var f = new Fixture(); f.Vault.Seed("configuration", Stored); f.Vault.Seed("refresh", "synthetic-preserved-refresh");
        using var service = new GoogleConnectionService(f.Store, f.Vault, f.Http, () => throw new InvalidOperationException("Bundle must not be read."));
        Assert.True(service.Status.Configured); Assert.True(service.Status.Connected);
        Assert.Null(service.Status.LastError); Assert.Empty(f.Vault.Mutations);
        using var cancel = new CancellationTokenSource();
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = service.ConnectAsync(uri => { opened.SetResult(uri); return Task.CompletedTask; }, cancel.Token);
        var uri = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("stored-fixture.apps.googleusercontent.com", Query(uri, "client_id"));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(Stored, f.Vault.Load("configuration"));
        Assert.Equal("synthetic-preserved-refresh", f.Vault.Load("refresh"));
        Assert.Empty(f.Vault.Mutations); Assert.Equal(0, f.Http.Requests);
    }

    [Fact]
    public void Invalid_stored_configuration_fails_without_silent_bundle_switch_or_token_mutation()
    {
        using var f = new Fixture(); f.Vault.Seed("configuration", "invalid fixture"); f.Vault.Seed("refresh", "synthetic-preserved-refresh");
        var reads = 0;
        using var service = new GoogleConnectionService(f.Store, f.Vault, f.Http, () => { reads++; return Bundle; });
        Assert.False(service.Status.Configured); Assert.False(service.Status.Connected);
        Assert.Equal("invalid_configuration", service.Status.LastError); Assert.Equal(0, reads);
        Assert.Equal("synthetic-preserved-refresh", f.Vault.Load("refresh"));
        Assert.Empty(f.Vault.Mutations); Assert.Equal(0, f.Http.Requests);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("invalid fixture", "invalid_configuration")]
    [InlineData("{\"installed\":{\"client_id\":\"fixture.apps.googleusercontent.com\",\"refresh_token\":\"synthetic-forbidden\"}}", "invalid_configuration")]
    public void Missing_or_invalid_bundle_never_claims_ready_or_connected(string? bundle, string? error)
    {
        using var f = new Fixture();
        using var service = new GoogleConnectionService(f.Store, f.Vault, f.Http, () => bundle);
        Assert.False(service.Status.Configured); Assert.False(service.Status.Connected);
        Assert.Equal(error, service.Status.LastError); Assert.Empty(f.Vault.Mutations); Assert.Equal(0, f.Http.Requests);
    }

    [Fact]
    public async Task Bundle_hydration_preserves_binding_and_disconnect_keeps_client_ready_for_reconnect()
    {
        using var f = new Fixture(); f.Vault.Seed("refresh", "synthetic-preserved-refresh");
        var directory = Path.Combine(f.Store.Root, "google"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "binding.json");
        var journal = JsonSerializer.Serialize(new { AccountId = "fixture-account", FolderId = "fixture-folder", FolderName = "Fixture folder", ProcessorId = f.Store.ProcessorId });
        File.WriteAllText(path, journal);
        using (var service = new GoogleConnectionService(f.Store, f.Vault, f.Http, () => Bundle))
        {
            Assert.True(service.Status.Connected); Assert.Equal("fixture-folder", service.Status.FolderId);
            Assert.Empty(f.Vault.Mutations); Assert.Equal(journal, File.ReadAllText(path));
            await service.DisconnectAsync();
            Assert.False(service.Status.Connected); Assert.True(service.Status.Configured);
            Assert.Null(f.Vault.Load("refresh")); Assert.Equal(journal, File.ReadAllText(path));
        }
        using var reopened = new GoogleConnectionService(f.Store, f.Vault, f.Http, () => Bundle);
        Assert.True(reopened.Status.Configured); Assert.False(reopened.Status.Connected);
        Assert.Equal("fixture-folder", reopened.Status.FolderId); Assert.Equal(0, f.Http.Requests);
        Assert.DoesNotContain(f.Vault.Mutations, x => x == "save:configuration");
    }

    static string Query(Uri uri, string name) => uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => Uri.UnescapeDataString(p[0]) == name).Select(p => Uri.UnescapeDataString(p[1])).Single();
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "vemo-bundle-independent-" + Guid.NewGuid()).Replace("/var/", "/private/var/");
        public LibraryStore Store { get; }
        public Vault Vault { get; } = new(); public RejectNetwork Http { get; } = new();
        public Fixture() => Store = new(root);
        public void Dispose() { Store.Dispose(); Directory.Delete(root, true); }
    }
    sealed class Vault : IGoogleCredentialVault
    {
        readonly Dictionary<string, string> values = [];
        public List<string> Mutations { get; } = [];
        public void Seed(string key, string value) => values[key] = value;
        public string? Load(string key) => values.GetValueOrDefault(key);
        public void Save(string key, string value) { Mutations.Add("save:" + key); values[key] = value; }
        public void Delete(string key) { Mutations.Add("delete:" + key); values.Remove(key); }
    }
    sealed class RejectNetwork : IGoogleHttpTransport
    {
        public int Requests;
        public Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
        { Interlocked.Increment(ref Requests); throw new InvalidOperationException("Network is forbidden in this fixture."); }
    }
}
