using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;
namespace Vemo.App.Tests;

public sealed class TranscriptProgressViewTests
{
    [AvaloniaFact]
    public void Plain_transcript_cards_preserve_every_character_and_have_no_timestamps()
    {
        var view = new TranscriptView(); var host = new Window { Content = view, Width = 600, Height = 400 }; host.Show();
        try
        {
            const string raw = "첫 문장입니다. 두 번째 문장입니다.\n\n다음 문단입니다.";
            view.SetDocument(new(raw, [])); Pump();
            var cards = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("transcript-card")).ToArray();
            Assert.True(cards.Length >= 2);
            Assert.Equal(raw, string.Concat(view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(x => x.Text)));
            Assert.Empty(view.GetVisualDescendants().OfType<Button>());
            Assert.All(cards, card => Assert.NotNull(card.Background));
        }
        finally { host.Close(); Pump(); }
    }
    [AvaloniaFact]
    public void Trusted_timestamp_button_seeks_exact_start_and_empty_state_is_centered()
    {
        var view = new TranscriptView(); var host = new Window { Content = view, Width = 600, Height = 400 }; host.Show();
        try
        {
            double? seek = null; view.SeekRequested += value => seek = value;
            view.SetDocument(new("실제 구간입니다.", [new(12.5, 19, "실제 구간입니다.")])); Pump();
            var button = Assert.Single(view.GetVisualDescendants().OfType<Button>());
            Assert.Equal("0:12", button.Content); Click(host, button); Assert.Equal(12.5, seek);
            view.SetDocument(new("", [])); Pump();
            var empty = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "아직 전사가 없습니다.");
            Assert.True(empty.IsEffectivelyVisible); Assert.Equal(HorizontalAlignment.Center, empty.HorizontalAlignment); Assert.Equal(VerticalAlignment.Center, empty.VerticalAlignment);
            Assert.Empty(view.GetVisualDescendants().OfType<Button>());
        }
        finally { host.Close(); Pump(); }
    }
    [AvaloniaFact]
    public void Progress_only_shows_real_asr_percent_and_notes_elapsed_is_indeterminate()
    {
        var view = new ProcessingProgressView(); var host = new Window { Content = view }; host.Show();
        try
        {
            view.Update(new("job", "transcribing", 37, TimeSpan.FromSeconds(65)), JobState.Transcribing); Pump();
            var bar = Assert.Single(view.GetVisualDescendants().OfType<ProgressBar>());
            Assert.False(bar.IsIndeterminate); Assert.Equal(37, bar.Value);
            Assert.Equal("전사 중 · 37%", Named<TextBlock>(host, "현재 처리 단계").Text);
            view.Update(new("job", "notes", 88, TimeSpan.FromSeconds(126)), JobState.GeneratingNotes); Pump();
            Assert.True(bar.IsIndeterminate); Assert.Equal("노트 생성 중", Named<TextBlock>(host, "현재 처리 단계").Text);
            Assert.Equal("경과 2:06", Named<TextBlock>(host, "처리 경과 시간").Text);
            view.Update(new("job", "transcribing", null, TimeSpan.Zero), JobState.Transcribing); Assert.True(bar.IsIndeterminate);
            view.Update(null, JobState.Completed); Assert.False(view.IsVisible);
        }
        finally { host.Close(); Pump(); }
    }
}
