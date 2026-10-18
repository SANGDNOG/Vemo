using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vemo.Core;
namespace Vemo.App;

public sealed class TranscriptView : UserControl
{
    readonly StackPanel rows = new() { Spacing = 18, Margin = new Thickness(0, 12) };
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
        var next = TranscriptDisplay.Rows(document).ToArray();
        if (displayed.SequenceEqual(next) && rows.Children.Count > 0) return;
        displayed = next; rows.Children.Clear();
        empty.IsVisible = next.Length == 0; scroll.IsVisible = next.Length > 0;
        if (next.Length == 0) return;
        foreach (var row in next)
        {
            var layout = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 16 };
            if (row.StartSeconds is { } start && double.IsFinite(start) && start >= 0)
            {
                var time = TimeSpan.FromSeconds(start);
                var stamp = new Button { Content = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : $"{(int)time.TotalMinutes}:{time.Seconds:00}", VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0, 3), MinHeight = 24, Foreground = NativeWhite.Accent, FontSize = 12 };
                stamp.Classes.Add("quiet"); NativeWhite.Label(stamp, "전사 구간 " + stamp.Content + " 재생 위치로 이동");
                stamp.Click += (_, _) => SeekRequested?.Invoke(start); layout.Children.Add(stamp);
            }
            var text = new SelectableTextBlock { Text = row.Text, TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 25, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(text, 1); layout.Children.Add(text);
            var card = new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), Background = NativeWhite.Sidebar, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(1), Child = layout };
            card.Classes.Add("transcript-card"); rows.Children.Add(card);
        }
    }
}
