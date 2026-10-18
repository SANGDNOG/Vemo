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
    }
}
