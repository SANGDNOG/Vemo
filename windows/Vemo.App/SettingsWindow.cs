using Avalonia.Controls;
using Avalonia.Input;
using Vemo.Core;
namespace Vemo.App;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(LibraryStore store, MobileFolderAdapter mobile, GoogleConnectionService? google = null, bool traySupported = false)
    {
        Title = "Vemo 설정"; Width = 800; Height = 820; MinWidth = 640; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var view = new SettingsView(store, mobile, Close, google, traySupported); Content = view;
        var drained = false;
        Closing += async (_, e) =>
        {
            if (drained) return;
            e.Cancel = true; await view.StopAsync(); drained = true; Close();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    // Reject known ownership conflicts before committing; registration is not transactional.
    internal static void ApplySettings(LibraryStore store, MobileFolderAdapter mobile, AppSettings updated)
    {
        var input = string.IsNullOrWhiteSpace(updated.InputDirectory) ? "" : SafeFiles.Full(updated.InputDirectory);
        var output = string.IsNullOrWhiteSpace(updated.OutputDirectory) ? "" : SafeFiles.Full(updated.OutputDirectory);
        var sync = string.IsNullOrWhiteSpace(updated.MobileDirectory) ? "" : SafeFiles.Full(updated.MobileDirectory);
        updated = updated with { InputDirectory = input, OutputDirectory = output, MobileDirectory = sync };
        if (output != "") SafeFiles.Disjoint(store.Root, output);
        if (input != "")
        {
            SafeFiles.Disjoint(store.Root, input);
            if (output != "") SafeFiles.Disjoint(input, output);
            if (sync != "") SafeFiles.Disjoint(input, sync);
        }
        if (sync != "")
        {
            SafeFiles.Disjoint(store.Root, sync);
            if (output != "") SafeFiles.Disjoint(output, sync);
            var marker = Path.Combine(sync, "processor.json");
            SafeFiles.NoLinks(marker);
            if (File.Exists(marker))
            {
                using var doc = JsonFormat.Strict(SafeFiles.Read(marker));
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["schema_version", "processor_id", "display_name"]))
                    throw new InvalidDataException("동기화 폴더의 processor.json 형식을 확인하세요.");
                if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("processor_id").GetString() != store.ProcessorId)
                    throw new InvalidDataException("다른 PC에 연결된 동기화 폴더입니다. 이 PC의 폴더를 선택하세요.");
            }
        }
        var previous = store.Settings;
        try
        {
            store.UpdateSettings(updated);
            if (sync != "") mobile.Register(sync);
        }
        catch
        {
            // Best-effort restoration; background readers can observe the intermediate settings.
            if (store.Settings != previous) store.UpdateSettings(previous);
            throw;
        }
    }

}
