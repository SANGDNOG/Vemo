using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Xunit;
using static Vemo.App.Tests.UiFixture;
using static Vemo.App.Tests.DragDropTestInput;

namespace Vemo.App.Tests;

public sealed class DragStartTests
{
    sealed class CaptureDragSource : IPlatformDragSource
    {
        public List<LibraryDragPayload> Payloads { get; } = [];
        public Task<DragDropEffects> DoDragDropAsync(PointerPressedEventArgs triggerEvent, IDataTransfer dataTransfer, DragDropEffects allowedEffects)
        {
            Assert.Equal(DragDropEffects.Move, allowedEffects);
            Payloads.Add(dataTransfer.TryGetValue(LibraryDropRules.Format)!);
            return Task.FromResult(DragDropEffects.None);
        }
    }

    [AvaloniaTheory]
    [InlineData("folder")]
    [InlineData("single")]
    [InlineData("multiple")]
    public async Task Pointer_drag_starts_only_after_threshold_and_sends_actual_selected_payload(string kind)
    {
        await using var ui = new UiFixture();
        using var scope = AvaloniaLocator.EnterScope();
        var capture = new CaptureDragSource();
        AvaloniaLocator.CurrentMutable.Bind<IPlatformDragSource>().ToConstant(capture);
        if (kind != "folder") ui.SelectJob();
        if (kind == "multiple") ui.SelectJob(1, RawInputModifiers.Control);
        var row = kind == "folder" ? Realize(ui.Folders, 0) : Realize(ui.Jobs, 0);
        var point = Center(ui.Window, row);
        ui.Window.MouseDown(point, MouseButton.Left);
        ui.Window.MouseMove(point + new Vector(1, 1), RawInputModifiers.LeftMouseButton);
        Pump();
        Assert.Empty(capture.Payloads);
        ui.Window.MouseMove(point + new Vector(40, 0), RawInputModifiers.LeftMouseButton);
        Pump();
        var payload = Assert.Single(capture.Payloads);
        Assert.Same(ui.Window, payload.Source);
        Assert.Equal(kind == "folder" ? ui.FolderId : null, payload.FolderId);
        Assert.Equal(kind == "folder" ? [] : kind == "multiple" ? new[] { ui.UnfiledId, ui.FiledId } : new[] { ui.UnfiledId }, payload.RecordingIds);
        ui.Window.MouseUp(point + new Vector(40, 0), MouseButton.Left);
        Pump();
        Assert.False(ui.Store.Get(ui.UnfiledId).Trashed);
    }
}
