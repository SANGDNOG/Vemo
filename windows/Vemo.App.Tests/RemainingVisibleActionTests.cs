using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class RemainingVisibleActionTests
{
    [AvaloniaFact]
    public async Task Completed_recording_export_button_writes_three_entries_and_keeps_source()
    {
        await using var ui = new UiFixture();
        var export = Path.Combine(Path.GetDirectoryName(ui.LibraryRoot)!, "exports");
        ui.Store.UpdateSettings(ui.Store.Settings with { OutputDirectory = export });
        ui.Store.SaveTranscript(ui.UnfiledId, "전사 원문");
        ui.Store.Complete(ui.UnfiledId, "# 노트\n\n내용");
        ui.Navigate(1);
        ui.SelectJob();
        var sourceHash = SafeFiles.Hash(ui.Store.AudioPath(ui.UnfiledId));
        Click(ui.Window, Button(ui.Window, "ZIP 내보내기"));
        await Until(() => ui.Store.Get(ui.UnfiledId).ExportPath != null, "Export button must create an archive.");
        var path = ui.Store.Get(ui.UnfiledId).ExportPath!;
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(new[] { "notes.md", "original.wav", "transcript.txt" }, zip.Entries.Select(e => e.FullName).Order());
        using var transcript = new StreamReader(zip.GetEntry("transcript.txt")!.Open());
        using var notes = new StreamReader(zip.GetEntry("notes.md")!.Open());
        Assert.Equal("전사 원문", transcript.ReadToEnd());
        Assert.Equal("# 노트\n\n내용", notes.ReadToEnd());
        Assert.Equal(sourceHash, SafeFiles.Hash(ui.Store.AudioPath(ui.UnfiledId)));
        Assert.Equal(sourceHash, SafeFiles.Hash(Path.Combine(Path.GetDirectoryName(ui.LibraryRoot)!, "source.wav")));
    }

    [AvaloniaFact]
    public async Task Purge_menu_cancel_preserves_owned_audio_and_confirm_removes_only_owned_copy()
    {
        await using var ui = new UiFixture();
        ui.Navigate(2);
        ui.SelectJob();
        var owned = ui.Store.AudioPath(ui.TrashId);
        var source = Path.Combine(Path.GetDirectoryName(ui.LibraryRoot)!, "source.wav");
        var hash = SafeFiles.Hash(source);
        RightClick(ui.Window, Realize(ui.Jobs, 0));
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "소유 사본 영구 삭제…");
        var dialog = ui.Dialog("소유 사본 영구 삭제");
        Click(dialog, Button(dialog, "취소"));
        await Until(() => ui.Window.OwnedWindows.Count == 0, "Cancel purge must close the confirmation.");
        Assert.True(File.Exists(owned));
        Assert.NotEqual(JobState.Purged, ui.Store.Get(ui.TrashId).State);
        RightClick(ui.Window, Realize(ui.Jobs, 0));
        ActivateMenu(ui.Window, ui.Jobs.ContextMenu!, "소유 사본 영구 삭제…");
        dialog = ui.Dialog("소유 사본 영구 삭제");
        Click(dialog, Button(dialog, "영구 삭제"));
        await Until(() => ui.Store.Get(ui.TrashId).State == JobState.Purged && ui.Jobs.ItemCount == 0, "Confirm must purge and remove the recording from trash.");
        Assert.False(File.Exists(owned));
        Assert.Equal(hash, SafeFiles.Hash(source));
        Assert.True(File.Exists(ui.Store.AudioPath(ui.UnfiledId)));
    }

    [AvaloniaFact]
    public async Task Queue_flyout_pause_and_resume_updates_settings_and_status()
    {
        await using var ui = new UiFixture(seedRecordings: false);
        var button = Button(ui.Window, "처리 ⋯");
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        var toggle = Assert.Single(flyout.Items.OfType<MenuItem>(), item => Equals(item.Header, "새 작업 일시중지 / 재개"));
        Assert.True(ui.Store.Settings.QueuePaused);
        Click(ui.Window, button);
        Assert.True(flyout.IsOpen);
        Assert.True(toggle.Focus());
        Key(ui.Window, Avalonia.Input.Key.Enter);
        await Until(() => !ui.Store.Settings.QueuePaused, "Queue action must resume new processing.");
        Click(ui.Window, button);
        Assert.True(flyout.IsOpen);
        Assert.True(toggle.Focus());
        Key(ui.Window, Avalonia.Input.Key.Enter);
        await Until(() => ui.Store.Settings.QueuePaused, "Queue action must pause new processing.");
        await ui.CloseAsync();
        using var reopened = new LibraryStore(ui.LibraryRoot);
        Assert.True(reopened.Settings.QueuePaused);
    }
}
