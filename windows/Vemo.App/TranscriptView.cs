using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vemo.Core;
namespace Vemo.App;

public sealed class TranscriptView : UserControl
{
    readonly StackPanel rows = new() { Spacing = 0, Margin = new Thickness(32, 29) };
    TranscriptRow[] displayed = [];
    readonly ScrollViewer scroll;
    readonly TextBlock empty = NativeWhite.Text("아직 전사가 없습니다.", "secondary");
    public event Action<double>? SeekRequested;
    public TranscriptView()
    {
        scroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.TextAlignment = TextAlignment.Center;
        empty.Margin = new Thickness(24); var body = new Grid(); body.Children.Add(scroll); body.Children.Add(empty); Content = body;
        NativeWhite.Label(this, "전사 문장");
    }
    public void SetDocument(TranscriptDocument document)
    {
        var merged = new List<TranscriptRow>();
        foreach (var row in TranscriptDisplay.Rows(document))
        {
            if (merged.Count > 0 && row.StartSeconds != null && merged[^1].StartSeconds == row.StartSeconds && merged[^1].Text.Length + row.Text.Length <= 320)
            {
                var previous = merged[^1]; merged[^1] = previous with { Text = previous.Text + row.Text, EndSeconds = Math.Max(previous.EndSeconds ?? 0, row.EndSeconds ?? 0) };
            }
            else merged.Add(row);
        }
        var next = merged.ToArray();
        if (displayed.SequenceEqual(next) && rows.Children.Count > 0) return;
        displayed = next; rows.Children.Clear();
        empty.IsVisible = next.Length == 0; scroll.IsVisible = next.Length > 0;
        if (next.Length == 0) return;
        foreach (var row in next)
        {
            var layout = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 19 };
            if (row.StartSeconds is { } start && double.IsFinite(start) && start >= 0)
            {
                var time = TimeSpan.FromSeconds(start);
                var stamp = new Button { Content = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : $"{(int)time.TotalMinutes}:{time.Seconds:00}", VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0, 3), MinWidth = 30, MinHeight = 0, Foreground = NativeWhite.Accent, FontSize = 10 };
                stamp.Classes.Add("quiet"); NativeWhite.Label(stamp, "전사 구간 " + stamp.Content + " 재생 위치로 이동");
                stamp.Click += (_, _) => SeekRequested?.Invoke(start); layout.Children.Add(stamp);
            }
            var text = new SelectableTextBlock { Text = row.Text.TrimEnd('\r', '\n'), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 25.35, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(text, 1); layout.Children.Add(text);
            var card = new Border { Padding = new Thickness(0, 19), Background = Brushes.White, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Child = layout };
            card.Classes.Add("transcript-row"); rows.Children.Add(card);
        }
    }
}
