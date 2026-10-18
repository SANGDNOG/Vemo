using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Vemo.App;

public sealed partial class MainWindow
{
    string? recordingPressId;
    Point recordingPressPoint;

    void RecordingActivationPressed(object? sender, PointerPressedEventArgs e)
    {
        recordingPressId = null;
        if (closing || dragging || settingsView == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(v => v is not ListBoxItem).Any(v => v is Button) == true) return;
        if (DragRow(e.Source)?.Content is not JobChoice choice) return;
        recordingPressId = choice.Job.Id; recordingPressPoint = e.GetPosition(this);
    }
    void RecordingActivationReleased(object? sender, PointerReleasedEventArgs e)
    {
        var id = recordingPressId; recordingPressId = null;
        if (id == null || closing || dragging || settingsView == null || e.InitialPressMouseButton != MouseButton.Left) return;
        var delta = e.GetPosition(this) - recordingPressPoint;
        var threshold = Application.Current?.PlatformSettings?.GetTapSize(e.Pointer.Type) ?? new Size(4, 4);
        if (Math.Abs(delta.X) > threshold.Width || Math.Abs(delta.Y) > threshold.Height || DragRow(e.Source)?.Content is not JobChoice choice || choice.Job.Id != id) return;
        Dispatcher.UIThread.Post(() => { if (closing || dragging || settingsView == null) return; CloseSettings(); Detail(); jobs.Focus(); });
    }
    void ProgressChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => { if (!closing) UpdateProgress(); });
    }
    void UpdateProgress()
    {
        var job = Current(); var current = runner.Progress;
        processingProgress.Update(current?.JobId == job?.Id ? current : null, job?.State ?? Vemo.Core.JobState.Queued);
        if (!string.IsNullOrWhiteSpace(transcript.Text)) processingProgress.IsVisible = false;
        notesProgress.Update(current?.JobId == job?.Id ? current : null, job?.State ?? Vemo.Core.JobState.Queued);
        if (!string.IsNullOrWhiteSpace(notes.Text)) notesProgress.IsVisible = false;
        renderedNotes.IsVisible = !notesProgress.IsVisible;
        processingProgress.SetPaused(store.Settings.QueuePaused); notesProgress.SetPaused(store.Settings.QueuePaused);
        queueProgress.IsVisible = current != null && current.Stage is "preparing" or "transcribing" or "notes" or "saving";
        if (queueProgress.IsVisible && current != null)
        {
            var percent = current.Stage == "transcribing" && current.Percent is { } p && double.IsFinite(p) && p is >= 0 and <= 100 ? current.Percent : null;
            queueProgress.IsIndeterminate = percent == null; queueProgress.Value = percent ?? 0;
            queueInfo.Text = (current.Stage == "notes" ? "노트 생성 중" : current.Stage == "saving" ? "결과 저장 중" : current.Stage == "preparing" ? "오디오 준비 중" : "전사 중") + (percent is { } n ? $" · {n:0}%" : "") + $" · 경과 {(int)current.Elapsed.TotalMinutes}:{current.Elapsed.Seconds:00}";
        }
    }
}
