using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
namespace Vemo.App;

internal static class Program
{
    [STAThread] public static void Main(string[] args) => VemoApplication.BuildApp().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
public sealed class VemoApplication : Application
{
    public static AppBuilder BuildApp()
    {
        var builder = AppBuilder.Configure<VemoApplication>();
        // Windows platform fallback otherwise resolves Korean semibold text to regular
        // Malgun Gothic. An explicit fallback preserves the requested font weight.
        if (OperatingSystem.IsWindows()) builder.With(new Avalonia.Media.FontManagerOptions
        {
            FontFallbacks = [new Avalonia.Media.FontFallback { FontFamily = new Avalonia.Media.FontFamily("Malgun Gothic") }]
        });
        return builder;
    }
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Vemo.App/")) { Source = new Uri("avares://Vemo.App/NativeWhite.axaml") });
    }
    public override void OnFrameworkInitializationCompleted() { if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow(); base.OnFrameworkInitializationCompleted(); }
}
