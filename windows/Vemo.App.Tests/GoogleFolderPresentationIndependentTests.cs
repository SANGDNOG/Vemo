using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

// Uses only synthetic folder metadata. Existing SettingsConnectionStateTests remain UI-worker owned.
public sealed class GoogleFolderPresentationIndependentTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Folder_picker_offers_only_eligible_roots_and_defaults_empty_result_to_new_folder(bool includeEligible)
    {
        await using var ui = new UiFixture(seedRecordings: false);
        var vault = new Vault(); var cloud = new FolderCloud();
        cloud.Roots.Add(new("foreign-root", "Other PC", Guid.NewGuid().ToString()));
        if (includeEligible) cloud.Roots.Add(new("eligible-root", "This PC", ui.Store.ProcessorId));
        using var service = new GoogleConnectionService(ui.Store, vault, cloud, () => null);
        var pane = new SettingsView(ui.Store, new MobileFolderAdapter(ui.Store), google: service);
        var host = new Window { Content = pane, Width = 900, Height = 800 }; host.Show(); Pump();
        try
        {
            var list = Named<Button>(host, "Drive 폴더 선택…"); list.BringIntoView(); Pump(); Click(host, list);
            await Until(() => cloud.Requests > 0 && !service.Status.Busy && list.IsEnabled, "Folder listing must complete.");
            var choices = Named<ComboBox>(host, "Vemo에서 접근 가능한 폴더 목록");
            var items = choices.Items.Cast<ComboBoxItem>().ToArray();
            Assert.Equal(includeEligible ? new[] { "eligible-root" } : [], items.Select(x => Assert.IsType<GoogleDriveFolder>(x.Tag).Id));
            Assert.All(items, x => Assert.Equal(ui.Store.ProcessorId, Assert.IsType<GoogleDriveFolder>(x.Tag).ProcessorId));
            if (!includeEligible)
            {
                var name = Named<TextBox>(host, "새 Google Drive 폴더 이름");
                Assert.True(name.IsEffectivelyVisible); Assert.False(string.IsNullOrWhiteSpace(name.Text));
                Assert.True(Named<Button>(host, "Drive 폴더 만들기").IsEnabled);
                var select = Named<Button>(host, "이 폴더 사용");
                Assert.False(select.IsEffectivelyVisible && select.IsEnabled);
            }
            Assert.True(service.Status.Connected); Assert.Null(service.Status.FolderId);
            Assert.Equal(0, cloud.Writes); Assert.Equal(0, vault.Mutations);
        }
        finally { await pane.StopAsync(); host.Close(); Pump(); }
    }

    [AvaloniaFact]
    public async Task Stale_foreign_selection_explains_PC_ownership_in_Korean_without_raw_error_or_mutation()
    {
        await using var ui = new UiFixture(seedRecordings: false);
        var vault = new Vault(); var cloud = new FolderCloud();
        cloud.Roots.Add(new("changing-root", "Previously eligible", ui.Store.ProcessorId));
        using var service = new GoogleConnectionService(ui.Store, vault, cloud, () => null);
        var pane = new SettingsView(ui.Store, new MobileFolderAdapter(ui.Store), google: service);
        var host = new Window { Content = pane, Width = 900, Height = 800 }; host.Show(); Pump();
        try
        {
            var list = Named<Button>(host, "Drive 폴더 선택…"); list.BringIntoView(); Pump(); Click(host, list);
            await Until(() => !service.Status.Busy && Named<ComboBox>(host, "Vemo에서 접근 가능한 폴더 목록").ItemCount == 1, "Eligible choice must load.");
            var choices = Named<ComboBox>(host, "Vemo에서 접근 가능한 폴더 목록"); choices.SelectedIndex = 0; Pump();
            cloud.Roots[0] = cloud.Roots[0] with { ProcessorId = Guid.NewGuid().ToString() };
            var select = Named<Button>(host, "이 폴더 사용"); select.BringIntoView(); Pump(); Click(host, select);
            await Until(() => !service.Status.Busy && service.Status.LastError != null, "Stale selection must fail safely.");
            var message = Named<TextBlock>(host, "설정 저장 상태").Text ?? "";
            var status = Named<TextBlock>(host, "Google 연결 상태").Text ?? "";
            Assert.DoesNotContain("processor_mismatch", message + status);
            Assert.DoesNotContain("Google connection:", message + status);
            Assert.Contains("PC", message); Assert.Matches("[가-힣]", message);
            Assert.True(message.Contains("다른", StringComparison.Ordinal) || message.Contains("이 PC", StringComparison.Ordinal));
            Assert.True(service.Status.Connected); Assert.Null(service.Status.FolderId);
            Assert.Equal(0, cloud.Writes); Assert.Equal("fixture-refresh", vault.Load("refresh")); Assert.Equal(0, vault.Mutations);
            Assert.False(File.Exists(Path.Combine(ui.LibraryRoot, "google", "binding.json")));
        }
        finally { await pane.StopAsync(); host.Close(); Pump(); }
    }

    [AvaloniaFact]
    public async Task Bound_PC_shows_current_folder_and_sync_without_advertising_unsupported_folder_change()
    {
        await using var ui = new UiFixture(seedRecordings: false);
        var directory = Path.Combine(ui.LibraryRoot, "google"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "binding.json");
        var binding = JsonSerializer.Serialize(new { AccountId = "fixture-account", FolderId = "bound-root", FolderName = "Current PC folder", ProcessorId = ui.Store.ProcessorId });
        File.WriteAllText(path, binding);
        var vault = new Vault(); var cloud = new FolderCloud();
        using var service = new GoogleConnectionService(ui.Store, vault, cloud, () => null);
        var pane = new SettingsView(ui.Store, new MobileFolderAdapter(ui.Store), google: service);
        var host = new Window { Content = pane, Width = 900, Height = 800 }; host.Show(); Pump();
        try
        {
            Assert.Contains("Current PC folder", Named<TextBlock>(host, "Google 선택한 폴더").Text);
            Assert.False(Named<Button>(host, "Drive 폴더 선택…").IsEffectivelyVisible);
            Assert.True(Named<Button>(host, "지금 동기화").IsEnabled);
            Assert.True(Named<Button>(host, "Google 연결 해제").IsEffectivelyVisible);
            Assert.Equal(binding, File.ReadAllText(path)); Assert.Equal(0, cloud.Requests); Assert.Equal(0, vault.Mutations);
        }
        finally { await pane.StopAsync(); host.Close(); Pump(); }
    }

    sealed class Vault : IGoogleCredentialVault
    {
        readonly Dictionary<string, string> values = new()
        {
            ["configuration"] = "{\"installed\":{\"client_id\":\"folder-ui-fixture.apps.googleusercontent.com\"}}",
            ["refresh"] = "fixture-refresh"
        };
        public int Mutations;
        public string? Load(string key) => values.GetValueOrDefault(key);
        public void Save(string key, string value) { Mutations++; values[key] = value; }
        public void Delete(string key) { Mutations++; values.Remove(key); }
    }
    sealed class FolderCloud : IGoogleHttpTransport
    {
        public List<GoogleDriveFolder> Roots { get; } = [];
        public int Requests, Writes;
        public async Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
        {
            Requests++; ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!; object result;
            if (uri.Host == "oauth2.googleapis.com") result = new { access_token = "fixture-access", token_type = "Bearer", expires_in = 3600 };
            else if (request.Method != HttpMethod.Get) { Writes++; throw new InvalidOperationException("This presentation test must never mutate Drive."); }
            else if (uri.AbsolutePath.EndsWith("/about")) result = new { user = new { permissionId = "fixture-account" } };
            else if (uri.AbsolutePath == "/drive/v3/files") result = new { files = Roots.Select(f => new { id = f.Id, name = f.Name, mimeType = GoogleDriveClient.FolderMime, parents = Array.Empty<string>(), properties = new Dictionary<string, string> { ["vemoProtocol"] = "1", ["vemoKind"] = "root", ["vemoProcessor"] = f.ProcessorId }, trashed = false }).ToArray(), incompleteSearch = false };
            else throw new InvalidOperationException("Unexpected fixture read.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result); Assert.True(bytes.Length <= maximumBytes);
            await receive(bytes, ct); return 200;
        }
    }
}
