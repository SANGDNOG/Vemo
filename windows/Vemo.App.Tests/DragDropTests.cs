using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Vemo.Core;
using Xunit;
using static Vemo.App.Tests.UiFixture;
using static Vemo.App.Tests.DragDropTestInput;

namespace Vemo.App.Tests;

public sealed class DragDropTests
{
    [AvaloniaFact]
    public async Task External_audio_raw_drop_imports_owned_copy_into_hovered_folder_and_preserves_original()
    {
        await using var ui = new UiFixture();
        var source = WritePcmWave(Path.GetDirectoryName(ui.LibraryRoot)!);
        var hash = SafeFiles.Hash(source);
        using var data = await Files(ui.Window, source);
        var target = Realize(ui.Folders, 0);
        var over = Route(target, data, DragDrop.DragOverEvent);
        Assert.Equal(DragDropEffects.Copy, over.DragEffects);
        RawDrop(ui.Window, target, data);
        await Until(() => ui.Store.Jobs.Count == 4, "Dropping supported local audio must import a recording.");
        var job = Assert.Single(ui.Store.Jobs, j => j.Title == "dropped");
        Assert.Equal(ui.FolderId, job.FolderId);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Equal(hash, job.AudioSha256);
        Assert.Equal(hash, SafeFiles.Hash(ui.Store.AudioPath(job.Id)));
        Assert.Equal(hash, SafeFiles.Hash(source));
        Assert.NotEqual(source, ui.Store.AudioPath(job.Id));
    }
    [AvaloniaFact]
    public async Task Folder_drag_moves_to_hovered_folder_then_root_without_moving_its_recordings()
    {
        await using var ui = new UiFixture();
        var parent = ui.Store.CreateFolder("프로젝트", null);
        await Until(() => ui.Folders.ItemCount == 2, "Timer must display the destination folder.");
        using (var data = Internal(ui, folderId: ui.FolderId))
        {
            var target = Folder(ui, "프로젝트");
            Assert.Equal(DragDropEffects.Move, Route(target, data, DragDrop.DragOverEvent).DragEffects);
            RawDrop(ui.Window, target, data);
            await Until(() => ui.Store.Folders.Single(f => f.Id == ui.FolderId).ParentId == parent.Id, "Folder drop must move the source folder.");
        }
        Assert.Equal(ui.FolderId, ui.Store.Get(ui.FiledId).FolderId);
        using (var data = Internal(ui, folderId: ui.FolderId))
        {
            var target = Realize(ui.Navigation, 0);
            Assert.Equal(DragDropEffects.Move, Route(target, data, DragDrop.DragOverEvent).DragEffects);
            RawDrop(ui.Window, target, data);
            await Until(() => ui.Store.Folders.Single(f => f.Id == ui.FolderId).ParentId == null, "Dropping folder on root must remove its parent.");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recordings_single_or_multiple_drag_into_folder_then_unfiled(bool multiple)
    {
        await using var ui = new UiFixture();
        var destination = ui.Store.CreateFolder("프로젝트", null);
        await Until(() => ui.Folders.ItemCount == 2, "Timer must display the destination.");
        var ids = multiple ? new[] { ui.UnfiledId, ui.FiledId } : new[] { ui.UnfiledId };
        using (var data = Internal(ui, recordingIds: ids))
        {
            var target = Folder(ui, "프로젝트");
            Assert.Equal(DragDropEffects.Move, Route(target, data, DragDrop.DragOverEvent).DragEffects);
            RawDrop(ui.Window, target, data);
            await Until(() => ids.All(id => ui.Store.Get(id).FolderId == destination.Id), "Recording drop must move each payload ID.");
        }
        using (var data = Internal(ui, recordingIds: ids))
        {
            var target = Realize(ui.Navigation, 1);
            Assert.Equal(DragDropEffects.Move, Route(target, data, DragDrop.DragOverEvent).DragEffects);
            RawDrop(ui.Window, target, data);
            await Until(() => ids.All(id => ui.Store.Get(id).FolderId == null), "Unfiled drop must clear recording folders.");
        }
        if (!multiple) Assert.Equal(ui.FolderId, ui.Store.Get(ui.FiledId).FolderId);
        Assert.True(ui.Store.Get(ui.TrashId).Trashed);
    }

    [AvaloniaTheory]
    [InlineData("self")]
    [InlineData("descendant")]
    [InlineData("trash")]
    [InlineData("same-parent")]
    public async Task Invalid_folder_drag_targets_reject_over_and_drop_without_mutation(string kind)
    {
        await using var ui = new UiFixture();
        ui.Store.CreateFolder("하위", ui.FolderId);
        await Until(() => ui.Folders.ItemCount == 2, "Timer must display the child folder.");
        var before = System.Text.Json.JsonSerializer.Serialize(ui.Store.Folders);
        using var data = Internal(ui, folderId: ui.FolderId);
        var target = kind switch
        {
            "self" => (Avalonia.Controls.Control)Folder(ui, "회의"),
            "descendant" => Folder(ui, "하위"),
            "trash" => Realize(ui.Navigation, 2),
            _ => Realize(ui.Navigation, 0)
        };
        Assert.Equal(DragDropEffects.None, Route(target, data, DragDrop.DragOverEvent).DragEffects);
        RawDrop(ui.Window, target, data);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(ui.Store.Folders));
    }

    [AvaloniaTheory]
    [InlineData("trash-target")]
    [InlineData("all-background")]
    [InlineData("all-target")]
    [InlineData("trashed-recording")]
    [InlineData("unknown-id")]
    [InlineData("different-window")]
    [InlineData("duplicate-id")]
    [InlineData("plain-text")]
    public async Task Invalid_recording_payload_or_target_cannot_mutate_library(string kind)
    {
        await using var ui = new UiFixture();
        var before = System.Text.Json.JsonSerializer.Serialize(ui.Store.Jobs);
        using var data = new DataTransfer();
        if (kind == "plain-text") data.Add(DataTransferItem.CreateText(ui.UnfiledId));
        else
        {
            var ids = kind == "unknown-id" ? new[] { Guid.NewGuid().ToString() } : kind == "trashed-recording" ? new[] { ui.TrashId } : kind == "duplicate-id" ? new[] { ui.UnfiledId, ui.UnfiledId } : kind is "all-background" or "all-target" ? new[] { ui.FiledId } : new[] { ui.UnfiledId };
            data.Add(DataTransferItem.Create(LibraryDropRules.Format, new LibraryDragPayload(kind == "different-window" ? new object() : ui.Window, null, ids)));
        }
        Avalonia.Controls.Control target = kind == "trash-target" ? Realize(ui.Navigation, 2) : kind == "all-target" ? Realize(ui.Navigation, 0) : kind == "all-background" ? ui.Jobs : Folder(ui, "회의");
        Assert.Equal(DragDropEffects.None, Route(target, data, DragDrop.DragOverEvent).DragEffects);
        RawDrop(ui.Window, target, data);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(ui.Store.Jobs));
    }

    [AvaloniaTheory]
    [InlineData("unsupported")]
    [InlineData("empty")]
    [InlineData("trash")]
    [InlineData("mixed")]
    public async Task External_invalid_audio_or_target_rejects_drop_and_preserves_all_sources(string kind)
    {
        await using var ui = new UiFixture();
        var directory = Path.GetDirectoryName(ui.LibraryRoot)!;
        var valid = WritePcmWave(directory);
        var invalid = Path.Combine(directory, kind == "empty" ? "empty.wav" : "unsupported.txt");
        File.WriteAllBytes(invalid, kind == "empty" ? [] : new byte[] { 1, 2, 3 });
        var paths = kind == "mixed" ? new[] { valid, invalid } : new[] { kind == "trash" ? valid : invalid };
        var hashes = paths.Select(SafeFiles.Hash).ToArray();
        using var data = await Files(ui.Window, paths);
        var target = kind == "trash" ? Realize(ui.Navigation, 2) : Folder(ui, "회의");
        Assert.Equal(DragDropEffects.None, Route(target, data, DragDrop.DragOverEvent).DragEffects);
        RawDrop(ui.Window, target, data);
        Assert.Equal(3, ui.Store.Jobs.Count);
        Assert.Equal(hashes, paths.Select(SafeFiles.Hash));
    }

    [AvaloniaFact]
    public async Task Import_invalid_folder_rejects_before_mutating_library_or_owned_items()
    {
        await using var ui = new UiFixture();
        var source = WritePcmWave(Path.GetDirectoryName(ui.LibraryRoot)!);
        var state = File.ReadAllBytes(Path.Combine(ui.LibraryRoot, "library.json"));
        var owned = Directory.GetFiles(Path.Combine(ui.LibraryRoot, "items"), "*", SearchOption.AllDirectories).Order().ToArray();
        var sourceHash = SafeFiles.Hash(source);
        Assert.ThrowsAny<Exception>(() => ui.Store.Import(source, folderId: Guid.NewGuid().ToString()));
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(ui.LibraryRoot, "library.json")));
        Assert.Equal(owned, Directory.GetFiles(Path.Combine(ui.LibraryRoot, "items"), "*", SearchOption.AllDirectories).Order());
        Assert.Equal(3, ui.Store.Jobs.Count);
        Assert.Equal(sourceHash, SafeFiles.Hash(source));
    }

    [AvaloniaFact]
    public async Task Import_returned_snapshot_already_contains_committed_destination_folder()
    {
        await using var ui = new UiFixture();
        var source = WritePcmWave(Path.GetDirectoryName(ui.LibraryRoot)!);
        var imported = ui.Store.Import(source, folderId: ui.FolderId);
        Assert.Equal(ui.FolderId, imported.FolderId);
        Assert.Equal(ui.FolderId, ui.Store.Get(imported.Id).FolderId);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ui.LibraryRoot, "library.json")));
        Assert.Equal(ui.FolderId, Assert.Single(json.RootElement.GetProperty("Jobs").EnumerateArray(), j => j.GetProperty("Id").GetString() == imported.Id).GetProperty("FolderId").GetString());
    }

}
