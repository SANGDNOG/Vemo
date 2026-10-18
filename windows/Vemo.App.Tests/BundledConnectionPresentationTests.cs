using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

// Independent bundle-to-presentation checks; existing Settings* tests belong to the UI worker.
public sealed class BundledConnectionPresentationTests
{
    const string Bundle = "{\"installed\":{\"client_id\":\"presentation-fixture.apps.googleusercontent.com\"}}";

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_settings_hide_client_setup_and_technical_explanations(bool bundled)
    {
        await using var ui = new UiFixture();
        var vault = new MemoryVault(); var network = new RejectNetwork();
        using var service = new GoogleConnectionService(ui.Store, vault, network, () => bundled ? Bundle : null);
        var pane = new SettingsView(ui.Store, new MobileFolderAdapter(ui.Store), google: service);
        var host = new Window { Content = pane, Width = 900, Height = 800 };
        host.Show(); Pump();
        try
        {
            var controls = pane.GetVisualDescendants().OfType<Control>().ToArray();
            var normalText = controls.Where(c => c.IsEffectivelyVisible).SelectMany(c => new[]
            {
                c is TextBlock text ? text.Text : null,
                c is ContentControl content ? content.Content as string : null,
                AutomationProperties.GetName(c), AutomationProperties.GetHelpText(c)
            }).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray();
            foreach (var text in normalText)
            foreach (var forbidden in new[] { "OAuth", "JSON", "Google Cloud", "클라이언트", "v1", "프로토콜", "즉시 적용", "whisper-cli.exe", "llama-cli.exe", "GGUF 모델 파일", "프리셋 설정 가져오기" })
                Assert.False(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "Normal settings exposes technical setup text: " + forbidden);
            Assert.DoesNotContain(controls.OfType<Button>(), b => b.Content is string s && (s.Contains("OAuth") || s.Contains("JSON")));
            Assert.Equal(bundled, Named<Button>(host, "Google 로그인").IsEnabled);
            Assert.Equal("Google Drive 연결", Named<Button>(host, "Google 로그인").Content);
            Assert.False(service.Status.Connected); Assert.False(service.Status.Busy);
            Assert.Equal(0, network.Requests); Assert.Equal(0, vault.Writes);
            Assert.NotNull(Named<Button>(host, "Google 연결 해제"));
            Assert.All(controls.OfType<Expander>(), x => Assert.False(x.IsExpanded));
        }
        finally { await pane.StopAsync(); host.Close(); Pump(); }
    }

    [AvaloniaFact]
    public async Task Explicit_disconnect_retained_with_bundle_and_preserves_account_binding()
    {
        await using var ui = new UiFixture();
        var vault = new MemoryVault(); vault.Values["refresh"] = "synthetic-ui-refresh";
        var network = new RejectNetwork();
        using var service = new GoogleConnectionService(ui.Store, vault, network, () => Bundle);
        var pane = new SettingsView(ui.Store, new MobileFolderAdapter(ui.Store), google: service);
        var host = new Window { Content = pane, Width = 900, Height = 800 };
        host.Show(); Pump();
        try
        {
            Assert.True(service.Status.Connected); Assert.Equal(0, vault.Writes);
            var disconnect = Named<Button>(host, "Google 연결 해제");
            Assert.True(disconnect.IsEffectivelyVisible && disconnect.IsEnabled);
            disconnect.BringIntoView(); Pump(); Click(host, disconnect);
            await Until(() => !service.Status.Connected && Named<Button>(host, "Google 로그인").IsEnabled, "Explicit disconnect must leave the bundled login action available.");
            Assert.True(service.Status.Configured); Assert.Null(vault.Load("refresh"));
            Assert.Equal(0, network.Requests);
        }
        finally { await pane.StopAsync(); host.Close(); Pump(); }
    }

    sealed class MemoryVault : IGoogleCredentialVault
    {
        public Dictionary<string, string> Values { get; } = [];
        public int Writes;
        public string? Load(string key) => Values.GetValueOrDefault(key);
        public void Save(string key, string value) { Writes++; Values[key] = value; }
        public void Delete(string key) { Writes++; Values.Remove(key); }
    }
    sealed class RejectNetwork : IGoogleHttpTransport
    {
        public int Requests;
        public Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
        { Requests++; throw new InvalidOperationException("This UI check must never send network requests."); }
    }
}
