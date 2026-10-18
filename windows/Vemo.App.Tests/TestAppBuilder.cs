using Avalonia;
using Avalonia.Headless;
using Vemo.App;

[assembly: AvaloniaTestApplication(typeof(Vemo.App.Tests.TestAppBuilder))]

namespace Vemo.App.Tests;

public static class TestAppBuilder
{
    // Run the shipped Application.Initialize, including Fluent and NativeWhite styles.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<VemoApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
