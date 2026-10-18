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
    public void Plain_transcript_rows_preserve_every_character_and_have_no_timestamps()
    {
        var view = new TranscriptView(); var host = new Window { Content = view, Width = 600, Height = 400 }; host.Show();
        try
        {
            const string raw = "첫 문장입니다. 두 번째 문장입니다.\n\n다음 문단입니다.";
            view.SetDocument(new(raw, [])); Pump();
            var cards = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("transcript-row")).ToArray();
            Assert.True(cards.Length >= 2);
            // Display suppresses trailing line terminators; raw export preservation is tested by native pipeline tests.
            Assert.Equal(raw.Replace("\r", "").Replace("\n", ""), string.Concat(view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(x => x.Text)).Replace("\r", "").Replace("\n", ""));
            Assert.Empty(view.GetVisualDescendants().OfType<Button>());
            Assert.All(cards, row => Assert.Equal(1, row.BorderThickness.Top));
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
    [AvaloniaFact]
    public void SpeakerProgressNamesMeasuredStageAndNeverClaimsOverallCompletion()
    {
        var view = new ProcessingProgressView(); var host = new Window { Content = view }; host.Show();
        try
        {
            view.Update(new("job", "speakers", 37, TimeSpan.Zero), JobState.Completed); Pump();
            Assert.True(view.IsVisible);
            Assert.Equal("화자 구간 분석 · 37%", Named<TextBlock>(host, "현재 처리 단계").Text);
            view.Update(new("job", "speakers", 100, TimeSpan.Zero), JobState.Completed); Pump();
            Assert.Equal("화자 군집 정리 중", Named<TextBlock>(host, "현재 처리 단계").Text);
            Assert.True(Assert.Single(view.GetVisualDescendants().OfType<ProgressBar>()).IsIndeterminate);
            view.Update(new("job", "completed", null, TimeSpan.Zero), JobState.Completed); Assert.False(view.IsVisible);
        }
        finally { host.Close(); Pump(); }
    }
    [AvaloniaFact]
    public void SpeakerLabelsRenameAndAmbiguousRowsPreserveOriginalSegments()
    {
        var view = new TranscriptView(); var host = new Window { Content = view }; host.Show();
        try
        {
            var transcript = new TranscriptDocument("first second", [new(0, 2, "first "), new(2, 4, "second")]);
            var document = SpeakerDocument.Create(new string('a', 64), transcript, [new(0, 2, "one")]).Rename("speaker_1", "진행자");
            string? rename = null; view.RenameRequested += id => rename = id;
            view.SetDocument(transcript, document); Pump();
            var button = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "진행자"));
            Click(host, button); Assert.Equal("speaker_1", rename);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "화자 미확인");
            Assert.Equal(transcript.Text, string.Concat(view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text)));
        }
        finally { host.Close(); Pump(); }
    }
}
