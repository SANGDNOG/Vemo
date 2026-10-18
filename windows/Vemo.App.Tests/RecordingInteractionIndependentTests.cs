using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Input;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class RecordingInteractionIndependentTests
{
    [AvaloniaFact]
    public async Task Genuine_pointer_click_on_already_selected_recording_closes_settings_and_keeps_selection()
    {
        await using var ui = new UiFixture();
        ui.Navigate(1); ui.SelectJob();
        var selected = ui.Jobs.SelectedItem;
        Click(ui.Window, Button(ui.Window, "설정"));
        Assert.Contains(ui.Window.GetVisualDescendants().OfType<SettingsView>(), x => x.IsEffectivelyVisible);

        // SelectJob uses actual headless MouseDown/MouseUp on the realized ListBoxItem.
        // The selected item is deliberately unchanged, so SelectionChanged cannot fix this case.
        ui.SelectJob(ui.Jobs.SelectedIndex);
        Assert.Equal(selected, ui.Jobs.SelectedItem);
        Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<SettingsView>(), x => x.IsEffectivelyVisible);
        Assert.True(Named<TextBox>(ui.Window, "녹음 제목").IsEffectivelyVisible);
        Assert.Equal(ui.Store.Get(ui.UnfiledId).Title, Named<TextBox>(ui.Window, "녹음 제목").Text);
    }

    [AvaloniaFact]
    public async Task Transcript_cards_use_real_pointer_seek_and_keep_unaligned_text_without_fake_timestamps()
    {
        var pane = new TranscriptView();
        const string raw = "First exact sentence. Second exact sentence.";
        pane.SetDocument(new(raw, [new(1.25, 5.46, "First exact sentence."), new(9, 12, "Second exact sentence.")]));
        var host = new Window { Content = pane, Width = 650, Height = 500 }; host.Show(); Pump();
        try
        {
            Assert.Equal(raw, string.Concat(pane.GetVisualDescendants().OfType<SelectableTextBlock>().Select(x => x.Text)));
            double? seek = null; pane.SeekRequested += x => seek = x;
            var buttons = pane.GetVisualDescendants().OfType<Button>().ToArray(); Assert.Equal(2, buttons.Length);
            Click(host, buttons[1]); Assert.Equal(9, seek);
            pane.SetDocument(new(raw, [])); Pump();
            Assert.Empty(pane.GetVisualDescendants().OfType<Button>());
            Assert.Equal(raw, string.Concat(pane.GetVisualDescendants().OfType<SelectableTextBlock>().Select(x => x.Text)));
        }
        finally { host.Close(); Pump(); }
    }

    [AvaloniaFact]
    public async Task Inline_title_round_trip_preserves_exact_offset_and_has_single_date_suffix()
    {
        const string stamp = "2026-10-18T00:05:00+14:00";
        await using var ui = new UiFixture(configure: store => store.SetRecordedAt(store.Jobs.Single(j => j.Title == "미분류 녹음").Id, stamp));
        ui.Navigate(1); ui.SelectJob();
        var title = Named<TextBox>(ui.Window, "녹음 제목");
        Assert.Equal("미분류 녹음 — 2026-10-18", title.Text); Assert.True(title.IsReadOnly);
        Assert.True(title.Focus()); Key(ui.Window, Avalonia.Input.Key.Enter);
        Assert.False(title.IsReadOnly); Assert.Equal("미분류 녹음", title.Text);
        title.SelectAll(); Type(ui.Window, title, "수정 제목 — 2026-10-18"); Key(ui.Window, Avalonia.Input.Key.Enter);
        await Until(() => ui.Store.Get(ui.UnfiledId).Title == "수정 제목", "Inline save must store the base title.");
        Assert.Equal(stamp, ui.Store.Get(ui.UnfiledId).RecordedAt);
        Assert.Equal("수정 제목 — 2026-10-18", title.Text); Assert.True(title.IsReadOnly);
        Assert.False(Named<TextBox>(ui.Window, "녹음 날짜").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Notes_progress_remains_indeterminate_even_if_accidental_percent_is_supplied()
    {
        var pane = new ProcessingProgressView();
        var host = new Window { Content = pane, Width = 650, Height = 300 }; host.Show(); Pump();
        try
        {
            pane.Update(new("fixture", "notes", 83, TimeSpan.FromSeconds(65)), JobState.GeneratingNotes); Pump();
            var bar = Named<ProgressBar>(host, "실제 처리 진행률");
            Assert.True(bar.IsIndeterminate);
            Assert.DoesNotContain("%", Named<TextBlock>(host, "현재 처리 단계").Text);
            Assert.Contains("1:05", Named<TextBlock>(host, "처리 경과 시간").Text);
            pane.Update(new("fixture", "completed", 100, TimeSpan.FromSeconds(66)), JobState.Completed); Pump();
            Assert.False(pane.IsEffectivelyVisible);
        }
        finally { host.Close(); Pump(); }
    }
}
