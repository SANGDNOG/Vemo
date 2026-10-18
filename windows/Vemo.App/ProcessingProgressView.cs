using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Vemo.Core;
namespace Vemo.App;

public sealed class ProcessingProgressView : UserControl
{
    readonly TextBlock stage = NativeWhite.Text("", "secondary"), elapsed = NativeWhite.Text("", "caption");
    readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 3, Foreground = NativeWhite.Accent, Background = NativeWhite.Divider };
    readonly TextBlock heading = NativeWhite.Text("전사를 정리하고 있어요");
    readonly Button pause = new();
    public event Action? PauseRequested;
    public void SetPaused(bool paused) => pause.Content = paused ? "새 작업 재개" : "새 작업 일시정지";
    public ProcessingProgressView()
    {
        var body = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(25, 35) };
        var icon = VemoIcons.Outline("text", 36); icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 18); body.Children.Add(icon);
        heading.FontSize = 17; heading.FontWeight = Avalonia.Media.FontWeight.SemiBold; heading.HorizontalAlignment = HorizontalAlignment.Center; heading.Margin = new Thickness(0, 0, 0, 9); body.Children.Add(heading);
        stage.FontSize = 12; stage.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(stage);
        elapsed.FontSize = 12; elapsed.HorizontalAlignment = HorizontalAlignment.Center; elapsed.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(elapsed);
        progress.Width = 160; progress.Height = 4; progress.Margin = new Thickness(0, 8, 0, 20); body.Children.Add(progress);
        pause.Content = "새 작업 일시정지"; pause.Click += (_, _) => PauseRequested?.Invoke(); pause.FontSize = 11; pause.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(pause);
        Content = new Border { Background = Avalonia.Media.Brushes.White, Child = body };
        NativeWhite.Label(stage, "현재 처리 단계"); NativeWhite.Label(elapsed, "처리 경과 시간"); NativeWhite.Label(progress, "실제 처리 진행률"); IsVisible = false;
    }
    public void Update(ProcessingProgress? value, JobState state)
    {
        IsVisible = value != null && (value.Stage == "speakers" || state is JobState.Transcribing or JobState.GeneratingNotes);
        if (!IsVisible || value == null) return;
        heading.Text = value.Stage == "speakers" ? "화자를 분석하고 있어요" : value.Stage == "notes" ? "노트를 정리하고 있어요" : "전사를 정리하고 있어요";
        var label = value.Stage switch { "speakers" => value.Percent >= 100 ? "화자 군집 정리 중" : "화자 구간 분석", "preparing" => "오디오 준비 중", "transcribing" => "전사 중", "notes" => "노트 생성 중", "saving" => "결과 저장 중", "completed" => "완료", "cancelled" => "취소됨", "failed" => "처리 실패", _ => "처리 중" };
        var percent = (value.Stage == "transcribing" || value.Stage == "speakers" && value.Percent < 100) && value.Percent is { } p && double.IsFinite(p) && p is >= 0 and <= 100 ? value.Percent : null;
        stage.Text = label + (percent is { } number ? $" · {number:0}%" : "");
        var time = value.Elapsed < TimeSpan.Zero ? TimeSpan.Zero : value.Elapsed;
        elapsed.Text = $"경과 {(int)time.TotalMinutes}:{time.Seconds:00}";
        progress.IsIndeterminate = percent == null; progress.Value = percent ?? 0;
    }
}
