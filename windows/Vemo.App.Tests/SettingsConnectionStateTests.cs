using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class SettingsConnectionStateTests
{
    const string Configuration = "{\"installed\":{\"client_id\":\"fixture.apps.googleusercontent.com\"}}";

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Connection_controls_reflect_configuration_without_browser_or_network(bool configured, bool connected)
    {
        await using var ui = new UiFixture();
        var vault = new MemoryVault();
        if (configured) vault.Save("configuration", Configuration);
        if (connected) vault.Save("refresh", "synthetic-test-refresh");
        var transport = new ControlledTransport();
        using var service = new GoogleConnectionService(ui.Store, vault, transport);
        var pane = new SettingsView(ui.Store, new MobileFolderAdapter(ui.Store), google: service);
        var host = new Window { Content = pane, Width = 900, Height = 800 };
        host.Show(); Pump();
        try
        {
            Assert.Equal(configured, service.Status.Configured);
            Assert.Equal(connected, service.Status.Connected);
            Assert.Equal(configured && !connected, Named<Button>(host, "Google 로그인").IsEnabled);
            Assert.Equal(connected, Named<Button>(host, "Drive 폴더 선택…").IsEnabled);
            Assert.Equal(connected, Named<Button>(host, "Google 연결 해제").IsEnabled);
            Assert.False(Named<Button>(host, "지금 동기화").IsEnabled);
            Assert.False(Named<Button>(host, "Google 작업 취소").IsVisible);
            Assert.Equal(connected ? "Google 연결됨" : configured ? "로그인이 필요합니다." : "Google OAuth 데스크톱 클라이언트 설정을 가져오세요.", Named<TextBlock>(host, "Google 연결 상태").Text);
            Assert.Equal("선택한 Drive 폴더 없음", Named<TextBlock>(host, "Google 선택한 폴더").Text);
            Assert.Equal(0, transport.Requests);
        }
        finally { await pane.StopAsync(); host.Close(); Pump(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Folder_request_cancel_or_error_updates_status_and_restores_controls(bool fail)
    {
        await using var ui = new UiFixture();
        var vault = new MemoryVault();
        vault.Save("configuration", Configuration); vault.Save("refresh", "synthetic-test-refresh");
        var transport = new ControlledTransport();
        using var service = new GoogleConnectionService(ui.Store, vault, transport);
        var pane = new SettingsView(ui.Store, new MobileFolderAdapter(ui.Store), google: service);
        var host = new Window { Content = pane, Width = 900, Height = 800 };
        host.Show(); Pump();
        try
        {
            Click(host, Named<Button>(host, "Drive 폴더 선택…"));
            await Until(() => transport.Requests == 1 && service.Status.Busy, "The UI must start the injected request.");
            Assert.Equal("Google 작업 중…", Named<TextBlock>(host, "Google 연결 상태").Text);
            Assert.False(Named<Button>(host, "Drive 폴더 선택…").IsEnabled);
            Assert.False(Button(host, "설정 저장").IsEnabled);
            if (fail) transport.Complete.SetResult(true);
            else Click(host, Named<Button>(host, "Google 작업 취소"));
            await Until(() => !service.Status.Busy && Named<Button>(host, "Drive 폴더 선택…").IsEnabled, "Cancelled or failed request must release busy state.");
            Assert.Equal(fail ? "fixture_failure" : "cancelled", service.Status.LastError);
            Assert.Contains(fail ? "fixture_failure" : "cancelled", Named<TextBlock>(host, "Google 연결 상태").Text);
            Assert.False(Named<Button>(host, "Google 작업 취소").IsVisible);
            Assert.True(Button(host, "설정 저장").IsEnabled);
            Assert.Equal(fail ? "Google connection: fixture_failure" : "작업을 취소했습니다.", Named<TextBlock>(host, "설정 저장 상태").Text);
            Assert.Equal("synthetic-test-refresh", vault.Load("refresh"));
        }
        finally { await pane.StopAsync(); host.Close(); Pump(); }
    }

    sealed class MemoryVault : IGoogleCredentialVault
    {
        readonly Dictionary<string, string> values = [];
        public string? Load(string key) => values.GetValueOrDefault(key);
        public void Save(string key, string value) => values[key] = value;
        public void Delete(string key) => values.Remove(key);
    }

    sealed class ControlledTransport : IGoogleHttpTransport
    {
        public int Requests;
        public TaskCompletionSource<bool> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<int> SendAsync(HttpRequestMessage request, long maximumBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> receive, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            await Complete.Task.WaitAsync(ct);
            throw new GoogleConnectionException("fixture_failure");
        }
    }
}
