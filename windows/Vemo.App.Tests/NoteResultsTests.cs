using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class NoteResultsTests
{
    const string Markdown = "# 회의 요약\n\n## 결정 사항\n- 첫 번째 결정\n### 후속 작업\n담당자를 확인합니다.\n출처: 전사 00:12";
    const string Transcript = "복사할 원문 전사입니다.\n두 번째 문장입니다.";

    static void Complete(UiFixture ui)
    {
        ui.Store.SaveTranscript(ui.UnfiledId, Transcript);
        ui.Store.Complete(ui.UnfiledId, Markdown);
        ui.Navigate(1); ui.SelectJob();
    }

    static void Notes(UiFixture ui)
    {
        var tab = Assert.Single(ui.Window.GetVisualDescendants().OfType<TabItem>(), t => Equals(t.Header, "노트"));
        Click(ui.Window, tab);
    }

    [AvaloniaFact]
    public async Task Notes_render_headings_and_bullets_without_raw_markdown_editor()
    {
        await using var ui = new UiFixture();
        Complete(ui); Notes(ui);
        var visible = ui.Window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToArray();
        var heading = Assert.Single(visible, t => t.Text == "회의 요약");
        var subheading = Assert.Single(visible, t => t.Text == "결정 사항");
        var body = Assert.Single(visible, t => t.Text == "담당자를 확인합니다.");
        Assert.True(heading.FontSize > subheading.FontSize && subheading.FontSize > body.FontSize);
        Assert.Contains(visible, t => t.Text == "•  첫 번째 결정");
        Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible && t.Text == Markdown);
    }

    [AvaloniaFact]
    public async Task Notes_at_minimum_window_size_keep_a_readable_scroll_viewport()
    {
        await using var ui = new UiFixture();
        ui.Window.Width = 980; ui.Window.Height = 540; Pump();
        Complete(ui); Notes(ui);
        var tab = Assert.Single(ui.Window.GetVisualDescendants().OfType<TabItem>(), t => Equals(t.Header, "노트"));
        var scroll = Assert.IsType<ScrollViewer>(tab.Content);
        Assert.True(scroll.Viewport.Height >= 60, $"Notes require a readable viewport at 980×540; actual height: {scroll.Viewport.Height}.");
        scroll.BringIntoView(); Pump();
        var copy = Button(ui.Window, "복사");
        copy.BringIntoView(); Pump();
        var point = copy.TranslatePoint(new Point(copy.Bounds.Width / 2, copy.Bounds.Height / 2), ui.Window);
        Assert.NotNull(point);
        Assert.InRange(point.Value.X, 0, ui.Window.ClientSize.Width);
        Assert.InRange(point.Value.Y, 0, ui.Window.ClientSize.Height);
        Click(ui.Window, copy);
        await Until(() => ui.Window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "선택한 결과를 복사했습니다."), "The footer must be reachable by scrolling at minimum size.");
        Assert.NotNull(ui.Window.Clipboard);
        Assert.Equal(Markdown, await ui.Window.Clipboard.TryGetTextAsync());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_uses_selected_tab_original_content_and_preserves_saved_results(bool notes)
    {
        await using var ui = new UiFixture();
        Complete(ui);
        if (notes) Notes(ui);
        var clipboard = ui.Window.Clipboard;
        Assert.NotNull(clipboard);
        await clipboard.SetTextAsync("clipboard sentinel");
        Click(ui.Window, Button(ui.Window, "복사"));
        await Until(() => ui.Window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "선택한 결과를 복사했습니다."), "Copy must complete through the visible action handler.");
        Assert.Equal(notes ? Markdown : Transcript, await clipboard.TryGetTextAsync());
        Assert.Equal(Markdown, ui.Store.ReadNotes(ui.UnfiledId));
        Assert.Equal(Transcript, ui.Store.ReadTranscript(ui.UnfiledId));
    }

    [AvaloniaFact]
    public async Task Empty_notes_has_help_and_disables_copy_and_export()
    {
        await using var ui = new UiFixture();
        ui.Navigate(1); ui.SelectJob(); Notes(ui);
        Assert.Contains(ui.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text != null && t.Text.Contains("아직 노트가 없습니다"));
        Assert.False(Button(ui.Window, "복사").IsEnabled);
        Assert.False(Button(ui.Window, "내보내기").IsEnabled);
    }
}
