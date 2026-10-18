using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;
namespace Vemo.App.Tests;

public sealed class BundledReleaseSmokeTests
{
    [AvaloniaFact]
    public async Task Packaged_client_enables_first_run_login_without_import_or_account_access()
    {
        if (Environment.GetEnvironmentVariable("VEMO_EXPECT_BUNDLED_GOOGLE") != "1") return;
        Assert.NotNull(GoogleBundledClientConfiguration.ReadFromAssembly(typeof(SettingsView).Assembly));
        await using var ui = new UiFixture(seedRecordings: false);
        var service = GoogleConnectionService.Shared(ui.Store);
        Assert.True(service.Status.Configured);
        Assert.False(service.Status.Connected);
        Assert.Null(service.Status.LastError);
        Assert.False(Directory.Exists(Path.Combine(ui.LibraryRoot, "google", "credentials")));
        Click(ui.Window, Button(ui.Window, "설정"));
        Assert.True(Named<Button>(ui.Window, "Google 로그인").IsEnabled);
        Assert.Equal("Google Drive 연결", Named<Button>(ui.Window, "Google 로그인").Content);
        Assert.False(service.Status.Busy);
        Assert.Null(service.Status.LastSync);
    }
}
