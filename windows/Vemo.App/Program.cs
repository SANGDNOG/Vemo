using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
namespace Vemo.App;

internal static class Program
{
    [STAThread] public static void Main(string[] args) => AppBuilder.Configure<VemoApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
public sealed class VemoApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Vemo.App/")) { Source = new Uri("avares://Vemo.App/NativeWhite.axaml") });
    }
    public override void OnFrameworkInitializationCompleted() { if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow(); base.OnFrameworkInitializationCompleted(); }
}
