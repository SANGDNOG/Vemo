using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vemo.Core;
using Xunit;

namespace Vemo.App.Tests;

internal sealed class UiFixture : IAsyncDisposable
{
    readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), "vemo-ui-" + Guid.NewGuid().ToString("N"));
    public string LibraryRoot { get; }
    public MainWindow Window { get; }
    public LibraryStore Store => Field<LibraryStore>("store");
    public ListBox Navigation => Named<ListBox>(Window, "라이브러리");
    public ListBox Folders => Named<ListBox>(Window, "폴더");
    public ListBox Jobs => Named<ListBox>(Window, "녹음 목록");
    public string UnfiledId { get; } = "";
    public string FiledId { get; } = "";
    public string TrashId { get; } = "";
    public string FolderId { get; } = "";

    public UiFixture(bool seedRecordings = true, Action<LibraryStore>? configure = null)
    {
        Directory.CreateDirectory(temporaryDirectory);
        // macOS /var is a symlink; LibraryStore intentionally rejects symlink paths.
        temporaryDirectory = new DirectoryInfo(temporaryDirectory).FullName.Replace("/var/", "/private/var/");
        LibraryRoot = Path.Combine(temporaryDirectory, "library");
        using (var seed = new LibraryStore(LibraryRoot))
        {
            seed.UpdateSettings(seed.Settings with { QueuePaused = true });
            if (seedRecordings)
            {
                var audio = Path.Combine(temporaryDirectory, "source.wav");
                File.WriteAllBytes(audio, [82, 73, 70, 70, 1]);
                FolderId = seed.CreateFolder("회의", null).Id;
                UnfiledId = seed.Import(audio, "미분류 녹음").Id;
                FiledId = seed.Import(audio, "회의 녹음").Id;
                seed.Move([FiledId], FolderId);
                TrashId = seed.Import(audio, "삭제한 녹음").Id;
                seed.Trash([TrashId]);
            }
            configure?.Invoke(seed);
        }
        Window = new MainWindow(LibraryRoot);
        Window.Show();
        Pump();
    }

    // Read-only access to the real window-owned store avoids a second library lock.
    // Controls are located through existing accessibility names, never private UI methods.
    T Field<T>(string name) => (T)typeof(MainWindow)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(Window)!;

    public static T Named<T>(Window window, string name) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), c => AutomationProperties.GetName(c) == name);
    public static Button Button(Window window, string label) =>
        Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, label));

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    public static void Click(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Pump();
        Assert.True(control.IsVisible && control.IsEffectivelyEnabled, "Control must be visible and enabled before clicking.");
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, "Control must be laid out before clicking.");
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left, modifiers);
        window.MouseUp(point.Value, MouseButton.Left, modifiers);
        Pump();
    }

    public static void RightClick(Window window, Control control)
    {
        Pump();
        Assert.True(control.IsVisible && control.IsEffectivelyEnabled);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Right);
        window.MouseUp(point.Value, MouseButton.Right);
        Pump();
    }
    public static MenuItem Menu(ContextMenu menu, string label) =>
        Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, label));
    public static void ActivateMenu(Window window, ContextMenu menu, string label)
    {
        Assert.True(menu.IsOpen, "A real context menu must be open before activating its action.");
        var item = Menu(menu, label);
        Assert.True(item.IsEffectivelyEnabled, "The selected menu action must be enabled.");
        Assert.True(item.Focus());
        Key(window, Avalonia.Input.Key.Enter);
    }
    public static void ChooseDestination(Window dialog, int index)
    {
        var choices = Named<ComboBox>(dialog, "이동할 폴더");
        Assert.True(choices.Focus());
        Key(dialog, Avalonia.Input.Key.Space);
        Key(dialog, Avalonia.Input.Key.Home);
        for (var i = 0; i < index; i++) Key(dialog, Avalonia.Input.Key.Down);
        Key(dialog, Avalonia.Input.Key.Enter);
        Assert.Equal(index, choices.SelectedIndex);
    }

    public static ListBoxItem Realize(ListBox list, int index)
    {
        Pump();
        Assert.InRange(index, 0, list.ItemCount - 1);
        // ItemsSource can update before the virtualizing panel's next layout pass.
        // Request the actual item, then complete layout before querying its container.
        list.ScrollIntoView(index);
        list.UpdateLayout();
        Pump();
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
        container.BringIntoView();
        list.UpdateLayout();
        Pump();
        // Refresh can recycle the first container during the layout pass.
        container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
        Assert.Equal(list.Items[index], container.Content);
        Assert.True(container.Bounds.Width > 0 && container.Bounds.Height > 0);
        return container;
    }
    public void Navigate(int index) => Click(Window, Realize(Navigation, index));
    public void SelectJob(int index = 0, RawInputModifiers modifiers = RawInputModifiers.None) => Click(Window, Realize(Jobs, index), modifiers);
    public void SelectFolder(int index = 0) => Click(Window, Realize(Folders, index));
    public static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        if (window.IsVisible) window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Pump();
    }
    public static void Type(Window window, TextBox box, string text)
    {
        Assert.True(box.Focus());
        window.KeyTextInput(text);
        Pump();
    }
    public string[] VisibleTitles() => Jobs.Items.Cast<object>().Select(item => item.ToString()!.Split(" · ")[0]).ToArray();
    public static async Task Until(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            Pump();
            await Task.Delay(10);
        }
        Assert.True(condition(), message);
    }
    public Window Dialog(string title) => Assert.Single(Window.OwnedWindows, w => w.Title == title);
    public async Task CloseAsync()
    {
        Window.Close();
        await Until(() => !Window.IsVisible, "Window must drain and close before deleting its temporary library.");
    }
    public async ValueTask DisposeAsync()
    {
        if (Window.IsVisible) await CloseAsync();
        Directory.Delete(temporaryDirectory, true);
    }
}
