using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class AdditionalActionTests
{
    [AvaloniaFact]
    public async Task Import_plus_has_accessible_name_and_folder_move_is_context_only()
    {
        await using var ui = new UiFixture();
        var import = Named<Button>(ui.Window, "오디오 파일 가져오기");
        Assert.Equal("+", import.Content);
        Assert.True(import.IsEffectivelyVisible && import.IsEnabled);
        Assert.InRange(import.Bounds.Width, 28, 48);
        Assert.InRange(import.Bounds.Height, 28, 48);
        Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "선택 폴더 이동…"));
        ui.SelectFolder();
        RightClick(ui.Window, Realize(ui.Folders, ui.Folders.SelectedIndex));
        Assert.True(Menu(ui.Folders.ContextMenu!, "선택 폴더 이동…").IsEnabled);
        Key(ui.Window, Avalonia.Input.Key.Escape);
    }

    [AvaloniaFact]
    public async Task Empty_library_repeated_navigation_preserves_empty_state_and_available_actions()
    {
        await using var ui = new UiFixture(seedRecordings: false);
        for (var repeat = 0; repeat < 3; repeat++)
        {
            foreach (var index in new[] { 1, 2, 0 })
            {
                ui.Navigate(index);
                Assert.Empty(ui.Jobs.Items);
                Assert.Empty(ui.Folders.Items);
                Assert.Equal(index, ui.Navigation.SelectedIndex);
                Assert.False(Menu(ui.Jobs.ContextMenu!, "작업 취소").IsEnabled);
                Assert.False(Menu(ui.Jobs.ContextMenu!, "기존 모델 설정으로 재시도").IsEnabled);
                Assert.True(Button(ui.Window, "+ 폴더").IsEnabled);
                Assert.True(Button(ui.Window, "설정").IsEnabled);
            }
        }
        Click(ui.Window, Button(ui.Window, "+ 폴더"));
        var dialog = ui.Dialog("새 폴더");
        Type(dialog, Named<TextBox>(dialog, "새 폴더 이름"), "첫 폴더");
        Click(dialog, Button(dialog, "만들기"));
        await Until(() => ui.Folders.ItemCount == 1, "Creating the first folder must update an empty library.");
        ui.SelectFolder();
        Assert.Empty(ui.VisibleTitles());
    }

    [AvaloniaFact]
    public async Task Folder_context_move_excludes_self_and_descendants_and_moves_to_selected_parent()
    {
        await using var ui = new UiFixture();
        var parent = ui.Store.CreateFolder("프로젝트", null);
        var child = ui.Store.CreateFolder("하위", ui.FolderId);
        await Until(() => ui.Folders.ItemCount == 3, "The real refresh timer must display added folders.");
        ui.SelectFolder(ui.Folders.Items.Cast<object>().ToList().FindIndex(f => f.ToString() == "회의"));
        RightClick(ui.Window, Realize(ui.Folders, ui.Folders.SelectedIndex));
        ActivateMenu(ui.Window, ui.Folders.ContextMenu!, "선택 폴더 이동…");
        var dialog = ui.Dialog("폴더 이동");
        var choices = Named<ComboBox>(dialog, "이동할 폴더");
        Assert.Equal(new[] { "미분류 / 최상위", "프로젝트" }, choices.Items.Cast<object>().Select(x => x.ToString()));
        ChooseDestination(dialog, 1);
        Click(dialog, Button(dialog, "이동"));
        await Until(() => ui.Store.Folders.Single(f => f.Id == ui.FolderId).ParentId == parent.Id, "Folder move must persist the selected parent.");
        Assert.Equal(ui.FolderId, ui.Store.Folders.Single(f => f.Id == child.Id).ParentId);
        Assert.Equal(new[] { "회의 녹음" }, ui.VisibleTitles());
        await ui.CloseAsync();
        using var reopened = new LibraryStore(ui.LibraryRoot);
        Assert.Equal(parent.Id, reopened.Folders.Single(f => f.Id == ui.FolderId).ParentId);
    }

    [AvaloniaFact]
    public async Task Right_click_unselected_recording_retargets_trash_to_clicked_row()
    {
        await using var ui = new UiFixture();
        ui.SelectJob(0);
        RightClick(ui.Window, Realize(ui.Jobs, 1));
        Assert.True(ui.Jobs.ContextMenu!.IsOpen);
        Assert.Equal(1, ui.Jobs.SelectedIndex);
        Assert.Single(ui.Jobs.SelectedItems!);
        Assert.Equal("회의 녹음", Named<TextBox>(ui.Window, "녹음 제목").Text);
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu, "휴지통으로 이동");
        await Until(() => ui.Store.Get(ui.FiledId).Trashed, "The context action must target the right-clicked recording.");
        Assert.False(ui.Store.Get(ui.UnfiledId).Trashed);
    }

    [AvaloniaFact]
    public async Task Right_click_selected_recording_preserves_multiple_selection_for_context_action()
    {
        await using var ui = new UiFixture();
        ui.SelectJob(0);
        ui.SelectJob(1, RawInputModifiers.Control);
        RightClick(ui.Window, Realize(ui.Jobs, 0));
        Assert.Equal(2, ui.Jobs.SelectedItems!.Count);
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "휴지통으로 이동");
        await Until(() => ui.Store.Get(ui.UnfiledId).Trashed && ui.Store.Get(ui.FiledId).Trashed, "Right-clicking a selected row must retain the selected action group.");
    }

    [AvaloniaFact]
    public async Task Right_click_folder_retargets_create_child_to_clicked_folder()
    {
        await using var ui = new UiFixture();
        var other = ui.Store.CreateFolder("프로젝트", null);
        await Until(() => ui.Folders.ItemCount == 2, "The refresh timer must realize the second folder.");
        var first = ui.Folders.Items.Cast<object>().ToList().FindIndex(f => f.ToString() == "회의");
        var target = ui.Folders.Items.Cast<object>().ToList().FindIndex(f => f.ToString() == "프로젝트");
        ui.SelectFolder(first);
        RightClick(ui.Window, Realize(ui.Folders, target));
        Assert.Equal(target, ui.Folders.SelectedIndex);
        ActivateMenu(ui.Window, ui.Folders.ContextMenu!, "새 하위 폴더…");
        var dialog = ui.Dialog("새 폴더");
        Type(dialog, Named<TextBox>(dialog, "새 폴더 이름"), "하위 폴더");
        Click(dialog, Button(dialog, "만들기"));
        await Until(() => ui.Store.Folders.Any(f => f.Name == "하위 폴더"), "Folder context action must create its child.");
        Assert.Equal(other.Id, ui.Store.Folders.Single(f => f.Name == "하위 폴더").ParentId);
        Assert.Null(ui.Store.Folders.Single(f => f.Id == ui.FolderId).ParentId);
    }

    [AvaloniaFact]
    public async Task Folder_manage_button_retargets_rename_and_collapse_preserves_child_for_drop()
    {
        await using var ui = new UiFixture();
        var child = ui.Store.CreateFolder("하위", ui.FolderId);
        var other = ui.Store.CreateFolder("프로젝트", null);
        await Until(() => ui.Folders.ItemCount == 3, "Timer must display both root folders and the child.");
        ui.SelectFolder(ui.Folders.Items.Cast<object>().ToList().FindIndex(f => f.ToString() == "회의"));
        Click(ui.Window, Named<Button>(ui.Window, "프로젝트 폴더 관리"));
        ActivateMenu(ui.Window, ui.Folders.ContextMenu!, "이름 변경…");
        var dialog = ui.Dialog("폴더 이름 변경");
        var name = Named<TextBox>(dialog, "폴더 이름");
        Assert.Equal("프로젝트", name.Text);
        name.SelectAll(); Type(dialog, name, "이름 변경됨");
        Click(dialog, Button(dialog, "저장"));
        await Until(() => ui.Store.Folders.Single(f => f.Id == other.Id).Name == "이름 변경됨", "Manage action must rename the clicked folder.");
        Assert.Equal("회의", ui.Store.Folders.Single(f => f.Id == ui.FolderId).Name);
        Click(ui.Window, Named<Button>(ui.Window, "회의 하위 폴더 접기"));
        await Until(() => ui.Folders.ItemCount == 2, "Collapse must hide descendants from the tree.");
        Assert.DoesNotContain(ui.Folders.Items.Cast<object>(), f => f.ToString() == "하위");
        Assert.Equal(ui.FolderId, ui.Store.Folders.Single(f => f.Id == child.Id).ParentId);
        using (var data = DragDropTestInput.Internal(ui, recordingIds: [ui.UnfiledId]))
        {
            var target = DragDropTestInput.Folder(ui, "회의");
            Assert.Equal(DragDropEffects.Move, DragDropTestInput.Route(target, data, DragDrop.DragOverEvent).DragEffects);
            DragDropTestInput.RawDrop(ui.Window, target, data);
            await Until(() => ui.Store.Get(ui.UnfiledId).FolderId == ui.FolderId, "Collapsed parent remains a valid drop destination.");
        }
        Click(ui.Window, Named<Button>(ui.Window, "회의 하위 폴더 펼치기"));
        await Until(() => ui.Folders.ItemCount == 3, "Expand must restore the same child.");
        Assert.Contains(ui.Folders.Items.Cast<object>(), f => f.ToString() == "하위");
    }

    [AvaloniaFact]
    public async Task Untouched_detail_title_tracks_background_store_rename()
    {
        await using var ui = new UiFixture();
        ui.SelectJob();
        Assert.Equal("미분류 녹음", Named<TextBox>(ui.Window, "녹음 제목").Text);
        await Task.Run(() => ui.Store.Rename(ui.UnfiledId, "백그라운드 갱신"));
        await Until(() => ui.VisibleTitles().Contains("백그라운드 갱신"), "Timer should update the recording list.");
        Assert.Equal("백그라운드 갱신", Named<TextBox>(ui.Window, "녹음 제목").Text);
    }

    [AvaloniaFact]
    public async Task User_title_draft_survives_timer_refresh_and_escape_reloads_stored_title()
    {
        await using var ui = new UiFixture();
        ui.SelectJob();
        var title = Named<TextBox>(ui.Window, "녹음 제목");
        title.Focus(); Key(ui.Window, Avalonia.Input.Key.Enter);
        title.SelectAll();
        Type(ui.Window, title, "저장하지 않은 제목");
        await Task.Run(() => ui.Store.Rename(ui.UnfiledId, "백그라운드 갱신"));
        await Until(() => ui.VisibleTitles().Contains("백그라운드 갱신"), "Timer must update the list without destroying a user draft.");
        Assert.Equal("저장하지 않은 제목", title.Text);
        Assert.Equal("백그라운드 갱신", ui.Store.Get(ui.UnfiledId).Title);
        Assert.True(title.Focus());
        Key(ui.Window, Avalonia.Input.Key.Escape);
        Assert.Equal("백그라운드 갱신", title.Text);
        Assert.Equal("백그라운드 갱신", ui.Store.Get(ui.UnfiledId).Title);
    }

    [AvaloniaTheory]
    [InlineData("create")]
    [InlineData("move")]
    [InlineData("settings")]
    public async Task Editor_survives_real_timer_refresh_and_preserves_selected_recording(string action)
    {
        await using var ui = new UiFixture();
        ui.SelectJob();
        if (action == "move")
        {
            RightClick(ui.Window, Realize(ui.Jobs, ui.Jobs.SelectedIndex));
            ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "선택 이동…");
        }
        else Click(ui.Window, Button(ui.Window, action == "create" ? "+ 폴더" : "설정"));
        var dialog = action == "settings" ? ui.Window : Assert.Single(ui.Window.OwnedWindows);
        var settings = action == "settings" ? SettingsTestInput.AssertOpen(ui) : null;
        TextBox? input = action == "create" ? Named<TextBox>(dialog, "새 폴더 이름") : action == "settings" ? Named<TextBox>(dialog, "자동 ZIP 저장 폴더") : null;
        if (input != null)
        {
            if (action == "settings") SettingsTestInput.Edit(ui, input, "draft-value");
            else Type(dialog, input, "draft-value");
        }
        // Mutate the real window-owned store from background work. Observe the
        // actual 1-second DispatcherTimer; never invoke the private Refresh method.
        await Task.Run(() => ui.Store.Rename(ui.UnfiledId, "백그라운드 갱신"));
        await Until(() => ui.VisibleTitles().Contains("백그라운드 갱신"), "A real timer refresh must run while the modal dialog remains open.");
        Assert.True(dialog.IsVisible);
        if (action == "settings") Assert.Same(settings, SettingsTestInput.AssertOpen(ui));
        else Assert.Same(dialog, Assert.Single(ui.Window.OwnedWindows));
        Assert.Single(ui.Jobs.SelectedItems!);
        Assert.Equal("백그라운드 갱신", Named<TextBox>(ui.Window, "녹음 제목").Text);
        if (input != null) Assert.Equal("draft-value", input.Text);
        Click(dialog, Button(dialog, action == "create" ? "만들기" : "취소"));
        await Until(() => ui.Window.OwnedWindows.Count == 0 && !SettingsTestInput.IsOpen(ui), "The editor must still respond after a background refresh.");
        if (action == "create") Assert.Contains(ui.Store.Folders, f => f.Name == "draft-value");
        Assert.Single(ui.Jobs.SelectedItems!);
    }

    [AvaloniaTheory]
    [InlineData(JobState.Queued, false, false, true)]
    [InlineData(JobState.Cancelled, false, true, false)]
    [InlineData(JobState.Failed, false, true, false)]
    [InlineData(JobState.Completed, false, false, false)]
    [InlineData(JobState.GeneratingNotes, false, false, true)]
    [InlineData(JobState.Cancelled, true, false, false)]
    public async Task Retry_and_cancel_enablement_follows_selected_job_state(JobState state, bool trashed, bool retry, bool cancel)
    {
        await using var ui = new UiFixture();
        if (state == JobState.Cancelled) ui.Store.CancelQueued([ui.UnfiledId]);
        if (state == JobState.Failed) ui.Store.Fail(ui.UnfiledId, "test failure");
        if (state is JobState.GeneratingNotes or JobState.Completed) ui.Store.SaveTranscript(ui.UnfiledId, "test transcript");
        if (state == JobState.Completed) ui.Store.Complete(ui.UnfiledId, "# test notes");
        if (trashed) ui.Store.Trash([ui.UnfiledId]);
        ui.Navigate(trashed ? 2 : 1);
        ui.SelectJob();
        Assert.Equal(state, ui.Store.Get(ui.UnfiledId).State);
        Assert.Equal(retry, Menu(ui.Jobs.ContextMenu!, "기존 모델 설정으로 재시도").IsEnabled);
        Assert.Equal(retry, Menu(ui.Jobs.ContextMenu!, "현재 모델 설정으로 재시도").IsEnabled);
        Assert.Equal(cancel, Menu(ui.Jobs.ContextMenu!, "작업 취소").IsEnabled);
    }

    [AvaloniaTheory]
    [InlineData("기존 모델 설정으로 재시도", "large-v3-turbo")]
    [InlineData("현재 모델 설정으로 재시도", "small")]
    public async Task Cancel_then_retry_menu_uses_requested_model_snapshot(string retryAction, string expectedProfile)
    {
        await using var ui = new UiFixture();
        ui.Navigate(1);
        ui.SelectJob();
        RightClick(ui.Window, Realize(ui.Jobs, 0));
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "작업 취소");
        await Until(() => ui.Store.Get(ui.UnfiledId).State == JobState.Cancelled, "Cancel action must cancel the selected queued recording.");
        ui.Store.UpdateSettings(ui.Store.Settings with { Models = ui.Store.Settings.Models with { WhisperProfile = "small" } });
        RightClick(ui.Window, Realize(ui.Jobs, 0));
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, retryAction);
        await Until(() => ui.Store.Get(ui.UnfiledId).State == JobState.Queued, "Retry action must return the selected recording to the paused queue.");
        Assert.Equal(expectedProfile, ui.Store.Get(ui.UnfiledId).Models.WhisperProfile);
        Assert.Equal(JobState.Queued, ui.Store.Get(ui.FiledId).State);
        Assert.Null(ui.Store.Get(ui.FiledId).PendingArtifactRevision);
    }
}
