using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Vemo.App;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Vemo.StartupTray.Tests.TestAppBuilder))]
namespace Vemo.StartupTray.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class StartupTrayTests
{
    const string Executable = @"C:\Program Files\Vemo\Vemo.App.exe";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_entry_writes_only_when_enabling(bool enabled)
    {
        object? entry = null; var writes = 0; var removes = 0;
        Assert.True(WindowsStartup.Apply(entry, Executable, enabled,
            () => { entry = WindowsStartup.Command(Executable); writes++; }, () => removes++));
        Assert.Equal(enabled ? 1 : 0, writes); Assert.Equal(0, removes);
        if (enabled) Assert.Equal("\"C:\\Program Files\\Vemo\\Vemo.App.exe\"", entry);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Vemo\Vemo.App.exe")]
    [InlineData("\"C:\\Other\\Vemo.App.exe\"")]
    [InlineData("\"C:\\Program Files\\Vemo\\Vemo.App.exe\" --other")]
    [InlineData("%LOCALAPPDATA%\\Vemo.exe")]
    [InlineData(42)]
    public void Foreign_or_modified_value_is_never_written_or_removed(object foreign)
    {
        foreach (var enabled in new[] { true, false })
            Assert.False(WindowsStartup.Apply(foreign, Executable, enabled,
                () => Assert.Fail("Foreign startup entry overwritten."), () => Assert.Fail("Foreign startup entry removed.")));
        Assert.False(WindowsStartup.IsOwned(foreign, Executable));
    }

    [Fact]
    public void Exact_current_executable_is_owned_case_insensitively_and_removed_only_on_disable()
    {
        var entry = WindowsStartup.Command(Executable).ToUpperInvariant(); var removes = 0;
        Assert.True(WindowsStartup.IsOwned(entry, Executable));
        Assert.True(WindowsStartup.Apply(entry, Executable, true, () => Assert.Fail("Unnecessary rewrite."), () => removes++));
        Assert.Equal(0, removes);
        Assert.True(WindowsStartup.Apply(entry, Executable, false, () => Assert.Fail("Unexpected write."), () => removes++));
        Assert.Equal(1, removes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\Vemo.dll")]
    [InlineData("C:\\Vemo.exe\" --injected.exe")]
    [InlineData("C:\\Vemo\n.exe")]
    public void Invalid_executable_cannot_form_a_startup_command(string? executable)
        => Assert.Throws<InvalidOperationException>(() => WindowsStartup.Command(executable));

    [Fact]
    public void NonWindows_public_api_never_accesses_registry()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Do not exercise the real user's Run key in tests.");
        Assert.False(WindowsStartup.IsEnabled);
        Assert.False(WindowsStartup.SetEnabled(true)); Assert.False(WindowsStartup.SetEnabled(false));
    }

    [AvaloniaFact]
    public void Unsupported_tray_never_hides_window_and_restore_unminimizes()
    {
        var window = new Window(); window.Show();
        try
        {
            using var tray = new TrayController(window, () => false, () => { }, () => { });
            Assert.False(tray.IsSupported); Assert.False(tray.Enabled); Assert.True(window.IsVisible);
            tray.Enabled = true; Assert.False(tray.Enabled); Assert.True(window.IsVisible);
            window.Hide(); tray.Enabled = false; Assert.False(window.IsVisible);
            window.WindowState = WindowState.Minimized; tray.ShowWindow();
            Assert.Equal(WindowState.Normal, window.WindowState);
            window.Hide(); tray.Dispose(); Assert.False(window.IsVisible);
            tray.ShowWindow(); Assert.False(window.IsVisible);
            tray.Dispose(); Assert.False(tray.IsSupported);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Earlier_closed_handler_can_disable_and_dispose_without_reopening_window()
    {
        var window = new Window();
        TrayController? tray = null;
        // Match MainWindow's registration order: its Closed handler runs before the tray's.
        window.Closed += (_, _) => { tray!.Enabled = false; tray.Dispose(); };
        using var controller = new TrayController(window, () => false, () => { }, () => { });
        tray = controller;
        var opened = 0; window.Opened += (_, _) => opened++;
        window.Show();
        window.Close();
        Assert.False(window.IsVisible);
        Assert.Equal(1, opened);
        Assert.False(controller.Enabled);
        controller.ShowWindow();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Tray_menu_invokes_real_callbacks_and_updates_pause_label()
    {
        var window = new Window(); window.Show(); var paused = false; var exits = 0;
        using var tray = new TrayController(window, () => paused, () => paused = !paused, () => exits++);
        try
        {
            var menu = (NativeMenu)typeof(TrayController).GetField("menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
            var open = Assert.IsType<NativeMenuItem>(menu.Items[0]); var pause = Assert.IsType<NativeMenuItem>(menu.Items[1]);
            var exit = Assert.IsType<NativeMenuItem>(menu.Items[3]);
            Assert.Equal("Vemo 열기", open.Header); Assert.Equal("새 작업 일시중지", pause.Header); Assert.Equal("종료", exit.Header);
            Click(pause); Assert.True(paused); Assert.Equal("새 작업 재개", pause.Header);
            Click(pause); Assert.False(paused); Assert.Equal("새 작업 일시중지", pause.Header);
            window.Hide(); Click(open); Assert.True(window.IsVisible);
            Click(exit); Assert.Equal(1, exits);
            tray.Dispose(); Click(exit); Click(pause); Assert.Equal(1, exits); Assert.False(paused);
        }
        finally { window.Close(); }
    }

    static void Click(NativeMenuItem item) => typeof(NativeMenuItem).GetInterfaces()
        .Single(x => x.Name == "INativeMenuItemExporterEventsImplBridge").GetMethod("RaiseClicked")!.Invoke(item, null);
}
