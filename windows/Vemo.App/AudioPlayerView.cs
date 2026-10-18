using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Vemo.App;

public sealed class AudioPlayerView : UserControl, IDisposable
{
    readonly AudioPlayback playback = new();
    readonly Button play = new() { Content = "▶", Width = 32, Height = 32, MinHeight = 0, Padding = new Thickness(0) };
    readonly Slider seek = new() { Minimum = 0, Maximum = 1, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#9C8BCB") };
    readonly TextBlock elapsed = new() { Text = "0:00", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = NativeWhite.Secondary };
    readonly TextBlock duration = new() { Text = "0:00", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = NativeWhite.Secondary };
    readonly TextBlock error = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = NativeWhite.Danger };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    bool updating, disposed;
    string? source;

    public AudioPlayerView()
    {
        play.Classes.Add("quiet");
        NativeWhite.Label(play, "오디오 재생"); NativeWhite.Label(seek, "오디오 재생 위치", "방향키로 재생 위치 이동");
        var row = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(play); Grid.SetColumn(elapsed, 1); row.Children.Add(elapsed);
        Grid.SetColumn(seek, 2); row.Children.Add(seek); Grid.SetColumn(duration, 3); row.Children.Add(duration);
        var body = new StackPanel { Spacing = 5 }; body.Children.Add(row); body.Children.Add(error);
        Content = new Border { BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, 1), Padding = new Thickness(0, 10), Child = body };
        play.Click += (_, _) => { if (playback.IsPlaying) playback.Pause(); else playback.Play(); Update(); };
        seek.ValueChanged += (_, _) => { if (!updating) { playback.Seek(TimeSpan.FromSeconds(seek.Value)); Update(); } };
        timer.Tick += (_, _) => Update();
        AttachedToVisualTree += (_, _) => { if (!disposed) timer.Start(); };
        DetachedFromVisualTree += (_, _) => { timer.Stop(); Stop(); };
        Update();
    }
    public void SetSource(string? path)
    {
        if (disposed) return;
        // Library refreshes may reapply the selected owned path while it is playing.
        if (source == path) return;
        source = path; playback.SetSource(path); Update();
    }
    public void Stop() { playback.Stop(); Update(); }
    void Update()
    {
        playback.Refresh();
        updating = true;
        try
        {
            seek.Maximum = Math.Max(0.001, playback.Duration.TotalSeconds);
            seek.Value = playback.Position.TotalSeconds;
            elapsed.Text = Format(playback.Position); duration.Text = Format(playback.Duration);
            play.Content = playback.IsPlaying ? "Ⅱ" : "▶";
            NativeWhite.Label(play, playback.IsPlaying ? "오디오 일시정지" : "오디오 재생");
            play.IsEnabled = seek.IsEnabled = !disposed && playback.HasSource;
            error.Text = playback.Error; error.IsVisible = playback.Error != null;
        }
        catch (Exception e) { playback.Stop(); error.Text = "오디오 재생 실패: " + e.Message; error.IsVisible = true; play.IsEnabled = seek.IsEnabled = false; }
        finally { updating = false; }
    }
    static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); playback.Dispose(); source = null; Update();
    }
}
