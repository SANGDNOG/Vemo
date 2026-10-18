using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task Pointer_navigation_recycles_realized_containers_and_filters_recordings()
    {
        await using var ui = new UiFixture();
        Assert.Equal(new[] { "미분류 녹음", "회의 녹음" }, ui.VisibleTitles());
        for (var repeat = 0; repeat < 3; repeat++)
        {
            ui.Navigate(1);
            Assert.Equal(new[] { "미분류 녹음" }, ui.VisibleTitles());
            ui.Navigate(2);
            Assert.Equal(new[] { "삭제한 녹음" }, ui.VisibleTitles());
            ui.SelectFolder();
            Assert.Equal(new[] { "회의 녹음" }, ui.VisibleTitles());
            ui.Navigate(0);
            Assert.Equal(new[] { "미분류 녹음", "회의 녹음" }, ui.VisibleTitles());
        }
    }

    [AvaloniaFact]
    public async Task Keyboard_navigation_can_change_selection_during_refresh()
    {
        await using var ui = new UiFixture();
        ui.Navigate(0);
        Key(ui.Window, Avalonia.Input.Key.Down);
        Assert.Equal(new[] { "미분류 녹음" }, ui.VisibleTitles());
        Key(ui.Window, Avalonia.Input.Key.Down);
        Assert.Equal(new[] { "삭제한 녹음" }, ui.VisibleTitles());
        Key(ui.Window, Avalonia.Input.Key.Up);
        Assert.Equal(new[] { "미분류 녹음" }, ui.VisibleTitles());
    }

    [AvaloniaTheory]
    [InlineData("라이브러리")]
    [InlineData("폴더")]
    [InlineData("녹음 목록")]
    public async Task Realized_item_template_accepts_null_when_container_content_is_cleared(string name)
    {
        await using var ui = new UiFixture();
        var list = Named<ListBox>(ui.Window, name);
        var container = Realize(list, 0);
        Assert.NotEmpty(container.GetVisualDescendants());
        // Same Content clear that ItemsControl.ClearContainerForItemOverride performs
        // during recycling. Do not call the template delegate or private Refresh directly.
        container.ClearValue(ContentControl.ContentProperty);
        Pump();
        Assert.NotNull(list.ItemTemplate);
        list.ScrollIntoView(list.Items[0]!);
        Pump();
    }

    [AvaloniaFact]
    public async Task Create_folder_click_submit_persists_and_can_be_navigated()
    {
        await using var ui = new UiFixture();
        Click(ui.Window, Button(ui.Window, "+ 폴더"));
        var dialog = ui.Dialog("새 폴더");
        Type(dialog, Named<TextBox>(dialog, "새 폴더 이름"), "프로젝트");
        Click(dialog, Button(dialog, "만들기"));
        await Until(() => ui.Store.Folders.Count == 2 && ui.Window.OwnedWindows.Count == 0, "Creating a folder should persist and close its dialog.");
        Pump();
        Assert.Contains(ui.Folders.Items.Cast<object>(), f => f.ToString() == "프로젝트");
        var index = ui.Folders.Items.Cast<object>().ToList().FindIndex(f => f.ToString() == "프로젝트");
        ui.SelectFolder(index);
        Assert.Empty(ui.VisibleTitles());
        var root = ui.LibraryRoot;
        await ui.CloseAsync();
        using var reopened = new LibraryStore(root);
        Assert.Contains(reopened.Folders, f => f.Name == "프로젝트");
    }

    [AvaloniaFact]
    public async Task Create_folder_cancel_and_duplicate_validation_leave_library_unchanged()
    {
        await using var ui = new UiFixture();
        Click(ui.Window, Button(ui.Window, "+ 폴더"));
        var dialog = ui.Dialog("새 폴더");
        Type(dialog, Named<TextBox>(dialog, "새 폴더 이름"), "취소할 폴더");
        Click(dialog, Button(dialog, "취소"));
        await Until(() => ui.Window.OwnedWindows.Count == 0, "Cancel should close the dialog.");
        Assert.Single(ui.Store.Folders);
        Click(ui.Window, Button(ui.Window, "+ 폴더"));
        dialog = ui.Dialog("새 폴더");
        Type(dialog, Named<TextBox>(dialog, "새 폴더 이름"), "회의");
        Click(dialog, Button(dialog, "만들기"));
        Assert.True(dialog.IsVisible);
        Assert.Single(ui.Store.Folders);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("error") && !string.IsNullOrWhiteSpace(t.Text));
        Key(dialog, Avalonia.Input.Key.Escape);
    }

    [AvaloniaFact]
    public async Task Settings_click_opens_real_settings_and_cancel_preserves_configuration()
    {
        await using var ui = new UiFixture();
        var before = ui.Store.Settings;
        Click(ui.Window, Button(ui.Window, "설정"));
        var dialog = Assert.IsType<SettingsWindow>(Assert.Single(ui.Window.OwnedWindows));
        var output = Named<TextBox>(dialog, "자동 ZIP 저장 폴더");
        Type(dialog, output, "/unused-test-output");
        Click(dialog, Button(dialog, "취소"));
        await Until(() => ui.Window.OwnedWindows.Count == 0, "Settings cancel should close its dialog.");
        Assert.Equal(before, ui.Store.Settings);
    }

    [AvaloniaFact]
    public async Task Settings_save_persists_valid_fields_through_save_button()
    {
        await using var ui = new UiFixture();
        Click(ui.Window, Button(ui.Window, "설정"));
        var dialog = Assert.Single(ui.Window.OwnedWindows);
        var timeout = Named<TextBox>(dialog, "프로세스별 시간 제한 · 분");
        timeout.SelectAll();
        Type(dialog, timeout, "60");
        Click(dialog, Button(dialog, "설정 저장"));
        await Until(() => ui.Window.OwnedWindows.Count == 0, "Valid settings should save and close.");
        Assert.Equal(60, ui.Store.Settings.Models.TimeoutMinutes);
        Assert.True(ui.Store.Settings.QueuePaused);
    }

    [AvaloniaFact]
    public async Task Settings_invalid_timeout_keeps_dialog_open_without_mutation()
    {
        await using var ui = new UiFixture();
        var before = ui.Store.Settings;
        Click(ui.Window, Button(ui.Window, "설정"));
        var dialog = Assert.Single(ui.Window.OwnedWindows);
        var timeout = Named<TextBox>(dialog, "프로세스별 시간 제한 · 분");
        timeout.SelectAll();
        Type(dialog, timeout, "0");
        Click(dialog, Button(dialog, "설정 저장"));
        Assert.True(dialog.IsVisible);
        Assert.Equal(before, ui.Store.Settings);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Classes.Contains("error") && !string.IsNullOrEmpty(t.Text));
        Click(dialog, Button(dialog, "취소"));
    }

    [AvaloniaFact]
    public async Task Settings_foreign_processor_rejection_preserves_configuration_and_marker()
    {
        await using var ui = new UiFixture();
        var before = ui.Store.Settings;
        var foreign = Path.Combine(Path.GetDirectoryName(ui.LibraryRoot)!, "foreign-sync");
        Directory.CreateDirectory(foreign);
        var marker = Path.Combine(foreign, "processor.json");
        var markerText = System.Text.Json.JsonSerializer.Serialize(new { schema_version = 1, processor_id = Guid.NewGuid().ToString(), display_name = "Other PC" });
        File.WriteAllText(marker, markerText);
        Click(ui.Window, Button(ui.Window, "설정"));
        var dialog = Assert.Single(ui.Window.OwnedWindows);
        Type(dialog, Named<TextBox>(dialog, "모바일 v1 로컬 동기화 폴더"), foreign);
        Click(dialog, Button(dialog, "설정 저장"));
        Assert.True(dialog.IsVisible);
        Assert.Equal(before, ui.Store.Settings);
        Assert.Equal(markerText, File.ReadAllText(marker));
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("다른 PC") == true);
        Click(dialog, Button(dialog, "취소"));
        await ui.CloseAsync();
        using var reopened = new LibraryStore(ui.LibraryRoot);
        Assert.Equal(before, reopened.Settings);
    }

    [AvaloniaFact]
    public async Task Recording_click_then_save_button_updates_title_and_recorded_date()
    {
        await using var ui = new UiFixture();
        ui.SelectJob();
        var title = Named<TextBox>(ui.Window, "녹음 제목");
        Assert.Equal("미분류 녹음", title.Text);
        title.SelectAll();
        Type(ui.Window, title, "수정한 제목");
        Type(ui.Window, Named<TextBox>(ui.Window, "녹음 날짜"), "2026-10-18T10:00:00+09:00");
        Click(ui.Window, Button(ui.Window, "저장"));
        await Until(() => ui.Store.Get(ui.UnfiledId).Title == "수정한 제목", "Save must update the selected recording.");
        Assert.Equal("2026-10-18T10:00:00+09:00", ui.Store.Get(ui.UnfiledId).RecordedAt);
        Assert.Equal(new[] { "수정한 제목", "회의 녹음" }, ui.VisibleTitles());
    }

    [AvaloniaFact]
    public async Task Multiple_selection_delete_key_trashes_and_context_menu_restores_recordings()
    {
        await using var ui = new UiFixture();
        ui.SelectJob();
        ui.SelectJob(1, RawInputModifiers.Control);
        Assert.Equal(2, ui.Jobs.SelectedItems!.Count);
        Key(ui.Window, Avalonia.Input.Key.Delete);
        await Until(() => ui.Store.Get(ui.UnfiledId).Trashed && ui.Store.Get(ui.FiledId).Trashed, "Delete should trash every selected recording.");
        Assert.Empty(ui.VisibleTitles());
        ui.Navigate(2);
        ui.SelectJob();
        Key(ui.Window, Avalonia.Input.Key.A, RawInputModifiers.Control);
        Assert.Equal(3, ui.Jobs.SelectedItems!.Count);
        var restore = Assert.Single(ui.Jobs.ContextMenu!.Items.OfType<MenuItem>(), m => Equals(m.Header, "복원"));
        Assert.True(restore.IsEnabled);
        ui.Jobs.ContextMenu.Open(ui.Jobs);
        Pump();
        // Keyboard menu navigation uses the actual popup and registered Click handler.
        restore.Focus();
        Key(ui.Window, Avalonia.Input.Key.Enter);
        await Until(() => ui.Store.Jobs.All(j => !j.Trashed), "Restore should restore the selected trash.");
    }

    [AvaloniaFact]
    public async Task Selected_recording_move_dialog_moves_into_custom_folder()
    {
        await using var ui = new UiFixture();
        ui.SelectJob();
        Click(ui.Window, Button(ui.Window, "선택 이동…"));
        var dialog = ui.Dialog("폴더 이동");
        var choices = Named<ComboBox>(dialog, "이동할 폴더");
        Assert.True(choices.Focus());
        Key(dialog, Avalonia.Input.Key.Space);
        Key(dialog, Avalonia.Input.Key.Down);
        Key(dialog, Avalonia.Input.Key.Enter);
        Assert.Equal(1, choices.SelectedIndex);
        Click(dialog, Button(dialog, "이동"));
        await Until(() => ui.Store.Get(ui.UnfiledId).FolderId == ui.FolderId, "Move should use the selected destination.");
        ui.Navigate(1);
        Assert.Empty(ui.VisibleTitles());
        ui.SelectFolder();
        Assert.Equal(new[] { "미분류 녹음", "회의 녹음" }, ui.VisibleTitles());
    }
}
