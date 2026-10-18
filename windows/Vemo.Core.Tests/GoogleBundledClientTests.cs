using System.Text;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class GoogleBundledClientTests
{
    static string Temp() => Path.Combine(OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath() : Path.GetTempPath(), "vemo-bundled-" + Guid.NewGuid());
    static GoogleFixtureTransport NoNetwork() => new((_, _) => throw new InvalidOperationException("No network expected."));
    [Fact] public async Task FreshUserAutoConfiguresWithoutImportOrPersistingPublicConfig()
    {
        var path = Temp();
        try
        {
            using var store = new LibraryStore(path); var vault = new GoogleMemoryVault(); int loads = 0;
            using var service = new GoogleConnectionService(store, vault, NoNetwork(), () => { loads++; return GoogleOAuthTests.Configuration; });
            Assert.True(service.Status.Configured); Assert.False(service.Status.Connected); Assert.Null(service.Status.LastError); Assert.Equal(1, loads); Assert.Null(vault.Load("configuration"));
            using var ct = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConnectAsync(uri => { Assert.Equal("accounts.google.com", uri.Host); Assert.Contains("fixture.apps.googleusercontent.com", Uri.UnescapeDataString(uri.Query)); ct.Cancel(); return Task.CompletedTask; }, ct.Token));
            Assert.Null(vault.Load("refresh")); Assert.Null(vault.Load("configuration"));
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact] public void ExplicitStoredConfigurationAndRefreshTakePrecedenceWithoutLoadingBundle()
    {
        var path = Temp();
        try
        {
            using var store = new LibraryStore(path); var vault = new GoogleMemoryVault();
            vault.Save("configuration", GoogleOAuthTests.Configuration); vault.Save("refresh", "stored-refresh-fixture");
            using var service = new GoogleConnectionService(store, vault, NoNetwork(), () => throw new InvalidOperationException("Stored config must win."));
            Assert.True(service.Status.Configured); Assert.True(service.Status.Connected); Assert.Null(service.Status.LastError);
            Assert.Equal(GoogleOAuthTests.Configuration, vault.Load("configuration")); Assert.Equal("stored-refresh-fixture", vault.Load("refresh"));
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact] public void BundleHydrationPreservesExistingRefreshAndMissingBundleSupportsDevBuild()
    {
        var path = Temp();
        try
        {
            using var store = new LibraryStore(path); var vault = new GoogleMemoryVault(); vault.Save("refresh", "existing-fixture");
            using (var service = new GoogleConnectionService(store, vault, NoNetwork(), () => GoogleOAuthTests.Configuration)) { Assert.True(service.Status.Configured); Assert.True(service.Status.Connected); Assert.Equal("existing-fixture", vault.Load("refresh")); }
            using (var service = new GoogleConnectionService(store, new GoogleMemoryVault(), NoNetwork(), () => null)) { Assert.False(service.Status.Configured); Assert.False(service.Status.Connected); Assert.Null(service.Status.LastError); }
        }
        finally { Directory.Delete(path, true); }
    }
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"web\":{\"client_id\":\"fixture.apps.googleusercontent.com\"}}")]
    [InlineData("{\"type\":\"service_account\",\"private_key\":\"fixture\"}")]
    [InlineData("{\"installed\":{\"client_id\":\"fixture.apps.googleusercontent.com\",\"refresh_token\":\"fixture\"}}")]
    [InlineData("{\"installed\":{\"client_id\":\"fixture.apps.googleusercontent.com\"},\"refresh_token\":\"fixture\"}")]
    public void MalformedOrAccountCredentialsNeverBecomeBundledConfiguration(string input)
    {
        var path = Temp();
        try
        {
            using var store = new LibraryStore(path); var vault = new GoogleMemoryVault();
            using var service = new GoogleConnectionService(store, vault, NoNetwork(), () => input);
            Assert.False(service.Status.Configured); Assert.False(service.Status.Connected); Assert.Equal("invalid_configuration", service.Status.LastError); Assert.Null(vault.Load("configuration"));
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact] public void NormalizedResourceStripsSuppliedEndpointsAndContainsOnlyInstalledIdentity()
    {
        var normalized = GoogleBundledClientConfiguration.NormalizeInstalledJson(GoogleOAuthTests.Configuration);
        Assert.DoesNotContain("evil.example", normalized); Assert.DoesNotContain("token_uri", normalized); Assert.DoesNotContain("auth_uri", normalized);
        Assert.Equal("fixture.apps.googleusercontent.com", GoogleOAuthConfiguration.ParseInstalledJson(normalized).ClientId);
        Assert.True(Encoding.UTF8.GetByteCount(normalized) < Encoding.UTF8.GetByteCount(GoogleOAuthTests.Configuration));
        Assert.Null(GoogleBundledClientConfiguration.ReadFromAssembly(typeof(GoogleBundledClientTests).Assembly));
    }
}
