using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Vemo.Core;

namespace Vemo.App;

public sealed partial class MainWindow
{
    PointerPressedEventArgs? dragPress;
    Point dragOrigin;
    LibraryDragPayload? dragCandidate;
    Control? dropHighlight;
    bool dragging;

    void WireDragDrop(Control listArea)
    {
        foreach (var list in new[] { folders, jobs })
        {
            list.AddHandler(PointerPressedEvent, DragPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, DragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, (_, _) => { dragPress = null; dragCandidate = null; }, RoutingStrategies.Tunnel, handledEventsToo: true);
            // Avalonia notifies the old capture's ancestors when capture is lost.
            // Only discard a pending gesture; native drag ownership is handled by DoDragDropAsync.
            list.AddHandler(PointerCaptureLostEvent, (_, _) => { dragPress = null; dragCandidate = null; }, handledEventsToo: true);
        }
        foreach (var target in new Control[] { folders, navigation, listArea })
        {
            DragDrop.SetAllowDrop(target, true);
            DragDrop.AddDragOverHandler(target, DragOver);
            DragDrop.AddDragEnterHandler(target, DragOver);
            DragDrop.AddDragLeaveHandler(target, (_, _) => HighlightDrop(null));
            DragDrop.AddDropHandler(target, Dropped);
        }
        ToolTip.SetTip(folders, "폴더나 선택한 녹음을 폴더 위로 끌어 이동 · 전체 녹음에 놓아 최상위로 이동");
    }

    static ListBoxItem? DragRow(object? source) => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();

    void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        dragPress = null; dragCandidate = null;
        if (closing || dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(v => v is not ListBoxItem).Any(v => v is Button) == true) return;
        var row = DragRow(e.Source);
        if (sender == folders && row?.Content is FolderChoice { Id: { } folderId })
            dragCandidate = new(this, folderId, []);
        else if (sender == jobs && row?.Content is JobChoice choice && !choice.Job.Trashed)
        {
            // Capture before ListBox's default pointer handler can collapse a multi-selection.
            var ids = row.IsSelected && e.KeyModifiers == KeyModifiers.None ? Selected().ToArray() : [choice.Job.Id];
            dragCandidate = new(this, null, ids);
        }
        if (dragCandidate != null) { dragPress = e; dragOrigin = e.GetPosition(this); }
    }

    async void DragMoved(object? sender, PointerEventArgs e)
    {
        if (dragPress == null || dragCandidate == null || dragging || closing) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { dragPress = null; dragCandidate = null; return; }
        var delta = e.GetPosition(this) - dragOrigin;
        var threshold = Application.Current?.PlatformSettings?.GetTapSize(e.Pointer.Type) ?? new Size(4, 4);
        if (Math.Abs(delta.X) <= threshold.Width && Math.Abs(delta.Y) <= threshold.Height) return;
        var press = dragPress; var payload = dragCandidate; dragPress = null; dragCandidate = null;
        dragging = true; e.Handled = true;
        try
        {
            if (payload.RecordingIds.Length > 0)
            {
                var wasRefreshing = refreshing; refreshing = true;
                try
                {
                    jobs.UnselectAll();
                    foreach (var choice in jobs.Items.OfType<JobChoice>().Where(j => payload.RecordingIds.Contains(j.Job.Id))) jobs.SelectedItems?.Add(choice);
                }
                finally { refreshing = wasRefreshing; }
                Detail();
            }
            using var transfer = new DataTransfer(); transfer.Add(DataTransferItem.Create(LibraryDropRules.Format, payload));
            await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move);
        }
        catch (Exception error) { if (!closing) status.Text = error.Message; }
        finally { dragging = false; HighlightDrop(null); if (!closing) Refresh(); }
    }

    bool DropTarget(object? sender, DragEventArgs e, out string? destination, out string targetView, out Control? highlight)
    {
        destination = null; targetView = ""; highlight = null;
        if (sender == folders || sender == navigation)
        {
            var list = (ListBox)sender;
            // Use coordinates: the routed source may be the list itself under native OLE drag/drop.
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(r => r.IsVisible && r.Bounds.Size.Width > 0 && new Rect(r.Bounds.Size).Contains(e.GetPosition(r)));
            if (row?.Content is not FolderChoice choice || choice.View == "trash") return false;
            destination = choice.Id; targetView = choice.View; highlight = row; return true;
        }
        if (view == "trash") return false;
        destination = view == "folder" ? selectedFolder : null; targetView = view; highlight = sender as Control; return true;
    }

    DragDropEffects DropEffect(object? sender, DragEventArgs e, out string? destination, out LibraryDragPayload? payload, out string[] paths, out Control? highlight)
    {
        destination = null; payload = null; paths = []; highlight = null;
        if (closing || importing || OwnedWindows.Any(w => w.IsVisible) || !DropTarget(sender, e, out destination, out var targetView, out highlight)) return DragDropEffects.None;
        payload = e.DataTransfer.TryGetValue(LibraryDropRules.Format);
        if (payload != null)
        {
            if (!ReferenceEquals(payload.Source, this)) return DragDropEffects.None;
            if (payload.FolderId == null && targetView == "all") return DragDropEffects.None;
            var valid = payload.FolderId != null
                ? payload.RecordingIds.Length == 0 && targetView is "folder" or "all" && LibraryDropRules.CanMoveFolder(store, payload.FolderId, destination)
                : LibraryDropRules.CanMoveRecordings(store, payload.RecordingIds, destination);
            return valid ? e.DragEffects & DragDropEffects.Move : DragDropEffects.None;
        }
        var files = e.DataTransfer.TryGetFiles()?.ToArray();
        if (files == null || files.Any(f => f is not IStorageFile || f.TryGetLocalPath() == null)) return DragDropEffects.None;
        paths = files.Select(f => f.TryGetLocalPath()!).ToArray();
        return LibraryDropRules.CanImportFiles(paths) ? e.DragEffects & DragDropEffects.Copy : DragDropEffects.None;
    }

    void HighlightDrop(Control? control)
    {
        if (dropHighlight == control) return;
        dropHighlight?.Classes.Remove("drop-target"); dropHighlight = control; dropHighlight?.Classes.Add("drop-target");
    }

    void DragOver(object? sender, DragEventArgs e)
    {
        try { e.DragEffects = DropEffect(sender, e, out _, out _, out _, out var highlight); HighlightDrop(e.DragEffects == DragDropEffects.None ? null : highlight); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { e.DragEffects = DragDropEffects.None; HighlightDrop(null); }
        e.Handled = true;
    }

    async void Dropped(object? sender, DragEventArgs e)
    {
        HighlightDrop(null); e.Handled = true;
        try
        {
            var effect = DropEffect(sender, e, out var destination, out var payload, out var paths, out _);
            e.DragEffects = effect;
            if (effect == DragDropEffects.None) return;
            await Execute(async () =>
            {
                work.Token.ThrowIfCancellationRequested();
                if (payload?.FolderId is { } folderId)
                {
                    if (!LibraryDropRules.CanMoveFolder(store, folderId, destination)) return;
                    store.MoveFolder(folderId, destination); status.Text = "폴더를 이동했습니다.";
                }
                else if (payload != null)
                {
                    if (!LibraryDropRules.CanMoveRecordings(store, payload.RecordingIds, destination)) return;
                    var ids = payload.RecordingIds.Where(id => store.Get(id).FolderId != destination).ToArray();
                    await MoveRecordings(ids, destination); status.Text = $"{ids.Length}개 녹음을 이동했습니다.";
                }
                else await ImportDroppedFiles(paths, destination);
            });
        }
        catch (Exception error) { e.DragEffects = DragDropEffects.None; if (!closing) status.Text = error.Message; }
    }

    async Task ImportDroppedFiles(string[] paths, string? destination)
    {
        if (importing || !LibraryDropRules.CanImportFiles(paths)) return;
        if (destination != null && !store.Folders.Any(f => f.Id == destination)) return;
        importing = true; UpdateActions(); var imported = 0;
        try
        {
            foreach (var path in paths)
            {
                work.Token.ThrowIfCancellationRequested();
                await Task.Run(() => store.Import(path, folderId: destination), work.Token);
                imported++;
            }
            status.Text = $"{imported}개 가져옴 · 원본은 유지됩니다. 녹음 날짜는 확인 후 입력하세요.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            status.Text = $"{imported}개 가져옴 · 가져오기 중단: {error.Message}";
        }
        finally { importing = false; UpdateActions(); }
    }

    async Task MoveRecordings(string[] ids, string? destination)
    {
        store.Move(ids, destination);
        foreach (var id in ids) if (store.Get(id).State == JobState.Completed && !store.Get(id).Trashed)
        {
            work.Token.ThrowIfCancellationRequested();
            try { await Task.Run(() => store.Export(id), work.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { store.SetExportError(id, error.Message); }
        }
    }
}
