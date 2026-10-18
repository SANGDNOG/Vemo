using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class RecordingTitleActionTests
{
    [AvaloniaFact]
    public async Task Explicit_title_menu_uses_saved_structured_notes_and_manual_edit_stays_inline()
    {
        await using var ui = new UiFixture();
        var id = ui.UnfiledId;
        ui.Store.SaveTranscript(id, "Exact source");
        File.WriteAllText(Path.Combine(ui.Store.WorkDirectory(id), "notes.json"), """
            {"schema_version":1,"topics":[{"heading":"Battery recycling economics","summary":"Main topic","conditions":[],"examples":[],"refs":[1]}]}
            """);
        ui.Store.Complete(id, "# Historical notes\n\nOriginal body");
        ui.Navigate(1); ui.SelectJob();
        RightClick(ui.Window, Realize(ui.Jobs, ui.Jobs.SelectedIndex));
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "저장된 노트로 제목 자동 생성");
        await Until(() => ui.Store.Get(id).Title == "Battery recycling economics", "Regenerate must use saved JSON.");
        Assert.Equal("# Historical notes\n\nOriginal body", ui.Store.ReadNotes(id));
        RightClick(ui.Window, Realize(ui.Jobs, ui.Jobs.SelectedIndex));
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "제목 수정");
        var title = Named<TextBox>(ui.Window, "녹음 제목");
        Assert.False(title.IsReadOnly);
        title.SelectAll(); Type(ui.Window, title, "Manual title"); Key(ui.Window, Avalonia.Input.Key.Enter);
        await Until(() => ui.Store.Get(id).Title == "Manual title", "Inline edit must save.");
        Assert.Equal(TitleOwnership.Manual, ui.Store.Get(id).TitleOwnership);
        Assert.True(title.IsReadOnly);
    }
}
