using System.Text;
using Vemo.Core;
using Xunit;
namespace Vemo.Core.Tests;

public sealed class GoogleWindowsFactAttribute : FactAttribute
{
    public GoogleWindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows current-user DPAPI."; }
}
public sealed class GoogleCredentialVaultTests
{
    [GoogleWindowsFact]
    public async Task NativeDpapiEncryptsFixturesAndHydratesServiceWithoutNetwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "vemo-dpapi-fixture-" + Guid.NewGuid());
        try
        {
            using var store = new LibraryStore(root);
            var directory = Path.Combine(root, "google", "credentials"); var vault = new GoogleWindowsCredentialVault(directory);
            var refresh = "fixture-only-" + Guid.NewGuid();
            vault.Save("configuration", GoogleOAuthTests.Configuration); vault.Save("refresh", refresh);
            Assert.Equal(refresh, vault.Load("refresh")); Assert.Equal(GoogleOAuthTests.Configuration, vault.Load("configuration"));
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var ciphertext = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                Assert.DoesNotContain(refresh, ciphertext); Assert.DoesNotContain("fixture-secret", ciphertext); Assert.DoesNotContain("fixture.apps.googleusercontent.com", ciphertext);
            }
            var network = new GoogleFixtureTransport((_, _) => throw new InvalidOperationException("Native vault test must never access network."));
            using (var service = new GoogleConnectionService(store, vault, network))
            { Assert.True(service.Status.Configured); Assert.True(service.Status.Connected); Assert.Null(service.Status.LastError); await service.DisconnectAsync(); }
            Assert.Null(vault.Load("refresh")); Assert.False(File.Exists(Path.Combine(directory, "refresh.dpapi")));
            using (var restored = new GoogleConnectionService(store, new GoogleWindowsCredentialVault(directory), network))
            { Assert.True(restored.Status.Configured); Assert.False(restored.Status.Connected); }
            vault.Delete("configuration"); Assert.Null(vault.Load("configuration"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
