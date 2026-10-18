using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

internal static class DragDropTestInput
{
    public static Point Center(Window window, Control target)
    {
        Pump();
        Assert.True(target.Bounds.Width > 0 && target.Bounds.Height > 0);
        return target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
    }

    public static DragEventArgs Route(Control target, IDataTransfer data, RoutedEvent<DragEventArgs> routedEvent)
    {
        var args = new DragEventArgs(routedEvent, data, target,
            new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), KeyModifiers.None)
        { DragEffects = DragDropEffects.Copy | DragDropEffects.Move };
        target.RaiseEvent(args);
        Pump();
        return args;
    }

    public static void RawDrop(Window window, Control target, IDataTransfer data)
    {
        var point = Center(window, target);
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy | DragDropEffects.Move);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy | DragDropEffects.Move);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy | DragDropEffects.Move);
        Pump();
    }

    public static async Task<DataTransfer> Files(Window window, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
            Assert.NotNull(file);
            data.Add(DataTransferItem.CreateFile(file));
        }
        return data;
    }

    public static DataTransfer Internal(UiFixture ui, string? folderId = null, params string[] recordingIds)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(LibraryDropRules.Format, new LibraryDragPayload(ui.Window, folderId, recordingIds)));
        return data;
    }

    public static ListBoxItem Folder(UiFixture ui, string name) => Realize(ui.Folders,
        ui.Folders.Items.Cast<object>().ToList().FindIndex(item => item.ToString() == name));

    public static string WritePcmWave(string directory, string name = "dropped.wav")
    {
        var path = Path.Combine(directory, name);
        const int sampleRate = 16000, samples = 1600;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate)));
        return path;
    }
}
