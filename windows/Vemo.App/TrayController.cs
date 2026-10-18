using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Vemo.App;

/// <summary>UI-thread tray lifetime. The caller alone decides whether closing may hide a window.</summary>
public sealed class TrayController : IDisposable
{
    readonly Window window;
    readonly Func<bool> paused;
    readonly NativeMenu menu = new();
    readonly NativeMenuItem pause = new();
    TrayIcon? icon;
    TrayIcons? collection;
    bool disposed, closed;
    public string? Error { get; private set; }
    // A real native menu exporter proves attachment to a platform implementation.
    // Windows' shell must also exist; headless and non-Windows sessions are unsupported.
    public bool IsSupported => !disposed && icon?.NativeMenuExporter != null && HasShell();
    public bool Enabled
    {
        get => IsSupported && icon!.IsVisible;
        set
        {
            if (disposed) return;
            if (value && IsSupported) icon!.IsVisible = true;
            else
            {
                if (icon != null) icon.IsVisible = false;
            }
        }
    }

    public TrayController(Window window, Func<bool> paused, Action toggle, Action exit)
    {
        this.window = window; this.paused = paused;
        var open = new NativeMenuItem("Vemo 열기"); open.Click += (_, _) => ShowWindow();
        pause.Click += (_, _) => { if (!disposed) { toggle(); UpdateMenu(); } };
        var quit = new NativeMenuItem("종료"); quit.Click += (_, _) => { if (!disposed) exit(); };
        menu.Items.Add(open); menu.Items.Add(pause); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
        menu.NeedsUpdate += MenuNeedsUpdate;
        UpdateMenu(); window.Closed += WindowClosed;
        if (!HasShell() || Application.Current is not { } app) return;
        try
        {
            collection = TrayIcon.GetIcons(app);
            if (collection == null) { collection = new TrayIcons(); TrayIcon.SetIcons(app, collection); }
            icon = new TrayIcon { IsVisible = false, ToolTipText = "Vemo", Menu = menu };
            collection.Add(icon);
            if (icon.NativeMenuExporter == null) { RemoveIcon(); return; }
            using var stream = AssetLoader.Open(new Uri("avares://Vemo.App/Assets/vemo-icon.png"));
            icon.Icon = new WindowIcon(stream);
            icon.Clicked += IconClicked;
            icon.IsVisible = true;
        }
        catch (Exception e) { Error = "트레이를 사용할 수 없습니다: " + e.Message; RemoveIcon(); }
    }
    void UpdateMenu() => pause.Header = paused() ? "새 작업 재개" : "새 작업 일시중지";
    void MenuNeedsUpdate(object? sender, EventArgs e) { if (!disposed) UpdateMenu(); }
    void IconClicked(object? sender, EventArgs e) => ShowWindow();
    void WindowClosed(object? sender, EventArgs e) { closed = true; Dispose(); }
    public void ShowWindow()
    {
        if (disposed || closed) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
    void RemoveIcon()
    {
        if (icon == null) return;
        icon.Clicked -= IconClicked; collection?.Remove(icon); icon.Dispose(); icon = null;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; window.Closed -= WindowClosed; menu.NeedsUpdate -= MenuNeedsUpdate; RemoveIcon();
    }
    static bool HasShell() => OperatingSystem.IsWindows() && FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string? windowName);
}
