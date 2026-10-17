using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Vemo.Core;
namespace Vemo.App;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(LibraryStore store, MobileFolderAdapter mobile)
    {
        Title = "Vemo settings — local processing"; Width = 740; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var s = store.Settings; var m = s.Models; var content = new StackPanel { Margin = new Thickness(18), Spacing = 8 }; Content = new ScrollViewer { Content = content };
        content.Children.Add(new TextBlock { Text = "Select downloaded models and native runtime executables. Inference stays local.\nProfiles are labels; you must select the matching model file. No automatic downloads.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        TextBox Field(string label, string value, bool folder = false, bool picker = false) { content.Children.Add(new TextBlock { Text = label }); var row = new Grid { ColumnDefinitions = new("*,Auto") }; var box = new TextBox { Text = value }; row.Children.Add(box); if (picker || folder) { var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) }; Grid.SetColumn(browse, 1); browse.Click += async (_, _) => { if (folder) { var selected = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = false }); if (selected.Count > 0) box.Text = selected[0].TryGetLocalPath(); } else { var selected = await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = false }); if (selected.Count > 0) box.Text = selected[0].TryGetLocalPath(); } }; row.Children.Add(browse); } content.Children.Add(row); return box; }
        ComboBox Choice(string label, string[] options, string selected) { content.Children.Add(new TextBlock { Text = label }); var box = new ComboBox { ItemsSource = options, SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch }; content.Children.Add(box); return box; }
        var output = Field("Automatic ZIP output folder (separate from library and mobile folder)", s.OutputDirectory, true);
        var sync = Field("Mobile v1 LOCAL synced folder (not Google OAuth)", s.MobileDirectory, true);
        var whisperProfile = Choice("Whisper profile", ["small", "large-v3-turbo"], m.WhisperProfile);
        var whisperExe = Field("whisper.cpp whisper-cli.exe", m.WhisperExecutable, picker: true); var whisperModel = Field("Whisper ggml model", m.WhisperModel, picker: true);
        var notesProfile = Choice("Study notes profile", ["Qwen3.5 9B GGUF", "Qwen3.5 27B GGUF"], m.NotesProfile);
        var llamaExe = Field("llama.cpp llama-cli.exe", m.LlamaExecutable, picker: true); var llamaModel = Field("Qwen GGUF model", m.LlamaModel, picker: true);
        var ffmpeg = Field("FFmpeg native executable (audio decoder)", m.FfmpegExecutable, picker: true);
        var compute = Choice("Compute mode", ["CPU", "CUDA"], m.Compute);
        var cuda = new CheckBox { Content = "I verified this runtime and NVIDIA device support CUDA (not tested by Vemo)", IsChecked = m.CudaConfirmed }; content.Children.Add(cuda);
        var layers = Field("GPU layers (partial offload; 27B may exceed 16 GB VRAM)", m.GpuLayers.ToString()); var timeout = Field("Per-process timeout in minutes", m.TimeoutMinutes.ToString());
        content.Children.Add(new TextBlock { Text = "Memory estimates depend on quantization/context: 9B Q4 typically needs 8–16 GB available RAM; 27B Q4 roughly 20–32 GB. These are estimates. CPU works without CUDA; speed and quality remain unmeasured on your PC. Long transcripts over 14,000 characters preserve ASR but need a later chunked-notes implementation.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var message = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; var save = new Button { Content = "Save settings" }; content.Children.Add(save); content.Children.Add(message);
        var import = new Button { Content = "Import preset settings…" };
        content.Children.Insert(1, import);
        import.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose provisioned settings.json", AllowMultiple = false });
                if (files.Count == 0) return;
                var path = files[0].TryGetLocalPath() ?? throw new IOException("Choose a local settings file.");
                var preset = PresetSettings.Load(path); var selection = preset.Models;
                whisperProfile.SelectedItem = selection.WhisperProfile; notesProfile.SelectedItem = selection.NotesProfile;
                whisperExe.Text = selection.WhisperExecutable; whisperModel.Text = selection.WhisperModel;
                llamaExe.Text = selection.LlamaExecutable; llamaModel.Text = selection.LlamaModel; ffmpeg.Text = selection.FfmpegExecutable;
                compute.SelectedItem = "CPU"; cuda.IsChecked = false; layers.Text = selection.GpuLayers.ToString(); timeout.Text = selection.TimeoutMinutes.ToString();
                if (!string.IsNullOrWhiteSpace(preset.OutputDirectory)) output.Text = preset.OutputDirectory;
                message.Text = "Preset loaded for review. Check the visible local paths/output folder, then Save settings. No executable was started; mobile sync settings were not imported.";
            }
            catch (Exception e) { message.Text = e.Message; }
        };
        save.Click += (_, _) =>
        {
            try
            {
                var updated = store.Settings with { OutputDirectory = output.Text?.Trim() ?? "", MobileDirectory = sync.Text?.Trim() ?? "", Models = new() { WhisperProfile = whisperProfile.SelectedItem?.ToString() ?? "large-v3-turbo", NotesProfile = notesProfile.SelectedItem?.ToString() ?? "Qwen3.5 9B GGUF", WhisperExecutable = whisperExe.Text?.Trim() ?? "", WhisperModel = whisperModel.Text?.Trim() ?? "", LlamaExecutable = llamaExe.Text?.Trim() ?? "", LlamaModel = llamaModel.Text?.Trim() ?? "", FfmpegExecutable = ffmpeg.Text?.Trim() ?? "", Compute = compute.SelectedItem?.ToString() ?? "CPU", CudaConfirmed = cuda.IsChecked == true, GpuLayers = int.Parse(layers.Text ?? "20"), TimeoutMinutes = int.Parse(timeout.Text ?? "120") } };
                store.UpdateSettings(updated); if (updated.MobileDirectory != "") mobile.Register(updated.MobileDirectory); Close();
            }
            catch (Exception e) { message.Text = e.Message; }
        };
    }
}
