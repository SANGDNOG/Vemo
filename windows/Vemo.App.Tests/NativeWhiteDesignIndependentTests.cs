using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

// Contract: approved A Native White prototype.css and exports/windows-detail.png,
// windows-settings.png. These inspect laid-out controls, not source strings or screenshots.
public sealed class NativeWhiteDesignIndependentTests
{
    static readonly Color White = Color.Parse("#FFFFFF"), Divider = Color.Parse("#E8E8ED"),
        Sidebar = Color.Parse("#F7F7FA"), Selected = Color.Parse("#F0ECFA");

    [AvaloniaFact]
    public void Transcript_is_white_divider_rows_with_reference_typography_and_no_rounded_cards()
    {
        var view = new TranscriptView();
        view.SetDocument(new("첫 문장입니다. 두 번째 문장입니다.", [new(0, 5.46, "첫 문장입니다."), new(9, 12, "두 번째 문장입니다.")]));
        var host = new Window { Content = view, Background = Brushes.White, Width = 700, Height = 500 }; host.Show(); Pump();
        try
        {
            var text = view.GetVisualDescendants().OfType<SelectableTextBlock>().ToArray(); Assert.Equal(2, text.Length);
            var rows = text.Select(x => x.GetVisualAncestors().OfType<Border>().First()).ToArray();
            Assert.All(rows, row =>
            {
                AssertWhiteOrTransparent(row.Background);
                Assert.Equal(new CornerRadius(0), row.CornerRadius);
                Assert.Equal(new Thickness(0, 1, 0, 0), row.BorderThickness);
                AssertColor(Divider, row.BorderBrush);
                Assert.Equal(19, row.Padding.Top); Assert.Equal(19, row.Padding.Bottom);
                Assert.Equal(0, row.Padding.Left); Assert.Equal(0, row.Padding.Right);
            });
            Assert.All(text, t => Assert.Equal(13, t.FontSize));
            Assert.All(view.GetVisualDescendants().OfType<Button>(), b => Assert.Equal(10, b.FontSize));
            // Adjacent divider rows share one white reading surface, without card gutters.
            Assert.InRange(Y(rows[1], host) - (Y(rows[0], host) + rows[0].Bounds.Height), -0.5, 0.5);
            Assert.Equal("첫 문장입니다. 두 번째 문장입니다.", string.Concat(text.Select(x => x.Text)));
        }
        finally { host.Close(); Pump(); }
    }

    [AvaloniaFact]
    public async Task Reading_header_orders_title_date_folder_player_then_compact_segments()
    {
        await using var ui = new UiFixture(configure: s => s.SetRecordedAt(s.Jobs.Single(j => j.Title == "회의 녹음").Id, "2026-10-18T10:00:00+09:00"));
        ui.Window.Width = 1280; ui.Window.Height = 780; Pump();
        ui.SelectFolder(); ui.SelectJob();
        var title = Named<TextBox>(ui.Window, "녹음 제목");
        var date = Named<TextBlock>(ui.Window, "녹음 날짜 표시");
        var folder = Assert.Single(ui.Window.GetVisualDescendants().OfType<TextBlock>(), x =>
            x.IsEffectivelyVisible && x.Text == "회의" && X(x, ui.Window) >= X(title, ui.Window));
        var player = Assert.Single(ui.Window.GetVisualDescendants().OfType<AudioPlayerView>());
        var tabs = Assert.Single(ui.Window.GetVisualDescendants().OfType<TabControl>());
        Assert.Equal(26, title.FontSize); Assert.Equal(11, date.FontSize);
        Assert.InRange(X(title, ui.Window), 551.5, 552.5);
        Assert.True(Y(title, ui.Window) < Y(date, ui.Window), "Date belongs below the title.");
        Assert.True(Y(date, ui.Window) < Y(folder, ui.Window), "Folder breadcrumb belongs below the recording metadata.");
        Assert.True(Y(folder, ui.Window) < Y(player, ui.Window));
        Assert.True(Y(player, ui.Window) < Y(tabs, ui.Window));
        Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<TextBlock>(), x => x.IsEffectivelyVisible &&
            x.Text?.Contains("Qwen3.5", StringComparison.Ordinal) == true && x.Text.Contains("CPU", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Tabs_form_content_width_segment_group_with_lavender_selected_surface()
    {
        await using var ui = new UiFixture(); ui.Navigate(1); ui.SelectJob(); Pump();
        var tabs = Assert.Single(ui.Window.GetVisualDescendants().OfType<TabControl>());
        var items = tabs.GetVisualDescendants().OfType<TabItem>().Where(x => x.IsEffectivelyVisible).ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.Equal(12, item.FontSize));
        var group = items[0].GetVisualAncestors().OfType<Border>().First(b => b.GetVisualDescendants().Contains(items[1]));
        AssertColor(Sidebar, group.Background); Assert.Equal(new CornerRadius(11), group.CornerRadius);
        Assert.InRange(group.Bounds.Width, items.Sum(x => x.Bounds.Width), items.Sum(x => x.Bounds.Width) + 16);
        Assert.InRange(Math.Abs(Y(items[0], ui.Window) - Y(items[1], ui.Window)), 0, 0.5);
        var selected = Assert.Single(items, x => x.IsSelected);
        Assert.Contains(selected.GetVisualDescendants().Prepend(selected).OfType<Control>(), c => ColorOf(Background(c)) == Selected);
        // Selection must move with genuine input, retaining the reference's filled segment.
        var other = Assert.Single(items, x => !x.IsSelected); Click(ui.Window, other);
        Assert.True(other.IsSelected);
        Assert.Contains(other.GetVisualDescendants().Prepend(other).OfType<Control>(), c => ColorOf(Background(c)) == Selected);
    }

    [AvaloniaFact]
    public async Task Settings_groups_use_white_outlines_internal_dividers_and_compact_heading()
    {
        await using var ui = new UiFixture(); Click(ui.Window, Button(ui.Window, "설정"));
        var pane = Assert.Single(ui.Window.GetVisualDescendants().OfType<SettingsView>());
        var heading = Assert.Single(pane.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "설정");
        Assert.Equal(25, heading.FontSize);
        var groups = pane.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("settings-group")).ToArray();
        Assert.NotEmpty(groups);
        Assert.All(groups, group =>
        {
            AssertColor(White, group.Background); AssertColor(Divider, group.BorderBrush);
            Assert.Equal(new Thickness(1), group.BorderThickness); Assert.Equal(new CornerRadius(13), group.CornerRadius);
            Assert.True(group.ClipToBounds);
            if (group.Child is Panel rows)
            {
                var visible = rows.Children.Where(x => x.IsVisible).ToArray();
                if (visible.FirstOrDefault() is Border first) Assert.Equal(0, first.BorderThickness.Top);
                if (visible.LastOrDefault() is Border last) Assert.Equal(0, last.BorderThickness.Bottom);
                foreach (var row in visible.OfType<Border>())
                {
                    Assert.Equal(0, row.BorderThickness.Left); Assert.Equal(0, row.BorderThickness.Right);
                    AssertColor(Divider, row.BorderBrush); AssertWhiteOrTransparent(row.Background);
                }
                for (int i = 1; i < visible.Length; i++)
                    if (visible[i - 1] is Border above && visible[i] is Border below)
                        Assert.Equal(1, above.BorderThickness.Bottom + below.BorderThickness.Top);
            }
        });
        Assert.Empty(ui.Window.OwnedWindows);
        Assert.True(ui.Navigation.IsEffectivelyVisible && ui.Jobs.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Reading_footer_has_three_neutral_actions_at_the_client_bottom()
    {
        await using var ui = new UiFixture(configure: s =>
        {
            s.UpdateSettings(s.Settings with { QueuePaused = false });
            var job = s.Jobs.Single(j => j.Title == "미분류 녹음");
            Assert.True(s.TryStart(job.Id)); s.SaveTranscript(job.Id, "원문입니다."); s.Complete(job.Id, "# 노트");
            s.UpdateSettings(s.Settings with { QueuePaused = true });
            s.SetRecordedAt(job.Id, "2026-10-18T10:00:00+09:00");
        });
        ui.Window.Width = 1280; ui.Window.Height = 780; Pump(); ui.Navigate(1); ui.SelectJob();
        var copy = Button(ui.Window, "복사"); var export = Button(ui.Window, "내보내기"); var folder = Button(ui.Window, "결과 폴더");
        var footer = copy.GetVisualAncestors().OfType<Panel>().First(p => p.GetVisualDescendants().Contains(export) && p.GetVisualDescendants().Contains(folder));
        Assert.Equal(3, footer.GetVisualDescendants().OfType<Button>().Count(x => x.IsEffectivelyVisible));
        foreach (var button in new[] { copy, export, folder })
        {
            AssertColor(Sidebar, button.Background); AssertColor(Divider, button.BorderBrush);
            Assert.Equal(new Thickness(1), button.BorderThickness);
            Assert.InRange(Y(button, ui.Window), 700, 744);
            Assert.True(Y(button, ui.Window) + button.Bounds.Height <= 780);
        }
    }

    [AvaloniaFact]
    public void Processing_state_uses_white_content_surface_and_reference_progress_bar()
    {
        var view = new ProcessingProgressView();
        var host = new Window { Content = view, Background = Brushes.White, Width = 700, Height = 400 };
        host.Show();
        try
        {
            view.Update(new("fixture", "transcribing", 37, TimeSpan.FromSeconds(65)), JobState.Transcribing); Pump();
            var stage = Named<TextBlock>(host, "현재 처리 단계");
            Assert.Contains("37%", stage.Text);
            Assert.All(stage.GetVisualAncestors().TakeWhile(x => x != view).OfType<Border>(), b => AssertWhiteOrTransparent(b.Background));
            var bar = Named<ProgressBar>(host, "실제 처리 진행률"); Assert.Equal(4, bar.Height); Assert.Equal(160, bar.Width);
            Assert.InRange(X(bar, host) + bar.Bounds.Width / 2, 349, 351);
            view.Update(new("fixture", "notes", null, TimeSpan.FromSeconds(66)), JobState.GeneratingNotes); Pump();
            Assert.True(bar.IsIndeterminate); Assert.DoesNotContain("%", stage.Text);
        }
        finally { host.Close(); Pump(); }
    }

    [AvaloniaFact]
    public void Notes_progress_names_notes_never_invents_percent_and_pause_action_tracks_state()
    {
        var view = new ProcessingProgressView();
        var host = new Window { Content = view, Width = 700, Height = 400 };
        var requested = 0;
        view.PauseRequested += () => { requested++; view.SetPaused(requested % 2 == 1); };
        host.Show();
        try
        {
            view.Update(new("fixture", "notes", 42, TimeSpan.FromSeconds(66)), JobState.GeneratingNotes); Pump();
            Assert.True(view.IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "노트를 정리하고 있어요");
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "전사를 정리하고 있어요");
            Assert.DoesNotContain("%", Named<TextBlock>(host, "현재 처리 단계").Text);
            Assert.True(Named<ProgressBar>(host, "실제 처리 진행률").IsIndeterminate);
            Click(host, Button(host, "새 작업 일시정지"));
            Assert.Equal(1, requested); Assert.True(Button(host, "새 작업 재개").IsEffectivelyVisible);
            Click(host, Button(host, "새 작업 재개"));
            Assert.Equal(2, requested); Assert.True(Button(host, "새 작업 일시정지").IsEffectivelyVisible);
            view.Update(null, JobState.Completed); Pump(); Assert.False(view.IsVisible);
        }
        finally { host.Close(); Pump(); }
    }

    static double X(Control c, Visual root) => c.TranslatePoint(new Point(), root)!.Value.X;
    static double Y(Control c, Visual root) => c.TranslatePoint(new Point(), root)!.Value.Y;
    static IBrush? Background(Control c) => c switch { Border b => b.Background, Panel p => p.Background, ContentPresenter p => p.Background, _ => null };
    static Color? ColorOf(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : null;
    static void AssertColor(Color expected, IBrush? brush) => Assert.Equal(expected, ColorOf(brush));
    static void AssertWhiteOrTransparent(IBrush? brush) => Assert.True(brush == null || ColorOf(brush) is { } c && (c.A == 0 || c == White), "Native White reading rows must not have a gray or tinted card fill.");
}
