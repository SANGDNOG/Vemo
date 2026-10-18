using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Vemo.Core;
namespace Vemo.App;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(LibraryStore store, MobileFolderAdapter mobile)
    {
        Title = "Vemo 설정 — 로컬 처리"; Width = 800; Height = 820; MinWidth = 640; MinHeight = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var closed = false; Closed += (_, _) => closed = true; KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        var s = store.Settings; var m = s.Models;
        var outer = new Grid { RowDefinitions = new("Auto,*,Auto") }; Content = outer;
        var header = new StackPanel { Margin = new Thickness(32, 26, 32, 18), Spacing = 8 }; header.Children.Add(NativeWhite.Text("설정", "heading")); header.Children.Add(NativeWhite.Text("로컬 처리기와 저장 위치를 설정합니다. 변경 내용은 저장한 뒤 적용됩니다.", "secondary")); outer.Children.Add(header);
        var content = new StackPanel { Margin = new Thickness(32, 0, 32, 24), Spacing = 20 }; var scroll = new ScrollViewer { Content = content }; Grid.SetRow(scroll, 1); outer.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(32, 16, 32, 20), Spacing = 12 }; var message = NativeWhite.Text("", "secondary"); NativeWhite.Label(message, "설정 저장 상태"); footer.Children.Add(new ScrollViewer { Content = message, MaxHeight = 100 });
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; footerButtons.Children.Add(NativeWhite.Action("취소", Close)); var save = new Button { Content = "설정 저장" }; save.Classes.Add("primary"); footerButtons.Children.Add(save); footer.Children.Add(footerButtons);
        var footerBorder = new Border { Child = footer, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(footerBorder, 2); outer.Children.Add(footerBorder);
        StackPanel group = new();
        void Section(string name, string help)
        {
            var section = new StackPanel { Spacing = 10 }; section.Children.Add(NativeWhite.Text(name, "secondary"));
            group = new StackPanel { Spacing = 12, Margin = new Thickness(18) }; group.Children.Add(NativeWhite.Text(help, "secondary"));
            section.Children.Add(new Border { Child = group, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = NativeWhite.Divider }); content.Children.Add(section);
        }
        var fieldErrors = new Dictionary<TextBox, TextBlock>();
        TextBox Field(string label, string value, bool folder = false, bool picker = false)
        {
            var field = new StackPanel { Spacing = 8 }; group.Children.Add(field); var caption = NativeWhite.Text(label); field.Children.Add(caption); var row = new Grid { ColumnDefinitions = new("*,Auto") }; var box = new TextBox { Text = value }; NativeWhite.Label(box, label); AutomationProperties.SetLabeledBy(box, caption); ToolTip.SetTip(box, label); row.Children.Add(box);
            if (picker || folder)
            {
                var browse = new Button { Content = "찾아보기…", Margin = new Thickness(8, 0, 0, 0) }; NativeWhite.Label(browse, label + " 찾아보기"); Grid.SetColumn(browse, 1);
                browse.Click += async (_, _) =>
                {
                    try
                    {
                        if (folder) { var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = label, AllowMultiple = false }); if (!closed && selected.Count > 0) box.Text = selected[0].TryGetLocalPath(); }
                        else { var selected = await StorageProvider.OpenFilePickerAsync(new() { Title = label, AllowMultiple = false }); if (!closed && selected.Count > 0) box.Text = selected[0].TryGetLocalPath(); }
                    }
                    catch (Exception e) { if (!closed) { message.Foreground = NativeWhite.Danger; message.Text = e.Message; } }
                };
                row.Children.Add(browse);
            }
            field.Children.Add(row);
            var error = NativeWhite.Text("", "error"); error.IsVisible = false; field.Children.Add(error); fieldErrors.Add(box, error);
            box.TextChanged += (_, _) => { error.IsVisible = false; }; return box;
        }
        ComboBox Choice(string label, string[] options, string selected)
        {
            var field = new StackPanel { Spacing = 8 }; group.Children.Add(field); var caption = NativeWhite.Text(label); field.Children.Add(caption); var box = new ComboBox { ItemsSource = options, SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch }; NativeWhite.Label(box, label); AutomationProperties.SetLabeledBy(box, caption); field.Children.Add(box); return box;
        }
        Section("입력 · 출력 폴더", "ZIP에는 원본 녹음, 전사 TXT, 노트 MD가 들어갑니다. 라이브러리와 모바일 폴더에서 분리된 저장 위치를 선택하세요.");
        var output = Field("자동 ZIP 저장 폴더", s.OutputDirectory, true);
        var sync = Field("모바일 v1 로컬 동기화 폴더", s.MobileDirectory, true);
        group.Children.Add(NativeWhite.Text("PC의 동기화 앱이 관리하는 폴더를 사용할 수 있습니다. Google 직접 로그인이나 업로드 확인 기능은 아닙니다.", "caption"));
        Section("로컬 모델 설정", "별도 설치 도구가 만든 settings.json을 불러오거나, 설치한 실행 파일과 모델을 직접 선택하세요. 모델을 자동 다운로드하거나 실행하지 않습니다.");
        var import = new Button { Content = "프리셋 설정 가져오기…" }; import.Classes.Add("soft"); group.Children.Add(import); var availability = NativeWhite.Text("", "secondary"); group.Children.Add(availability);
        Section("전사 · Whisper", "프로필 이름과 실제 모델 파일을 각각 선택합니다. 새 설정은 기존 작업의 모델 스냅샷을 바꾸지 않습니다.");
        var whisperProfile = Choice("전사 모델", ["small", "large-v3-turbo"], m.WhisperProfile);
        var whisperExe = Field("whisper.cpp · whisper-cli.exe", m.WhisperExecutable, picker: true); var whisperModel = Field("Whisper ggml 모델 파일", m.WhisperModel, picker: true);
        Section("주제별 노트 · Qwen", "9B와 27B는 필요한 메모리와 처리 시간이 다릅니다. 파일 선택은 이 PC에서의 실행 성공을 보장하지 않습니다.");
        var notesProfile = Choice("노트 모델", ["Qwen3.5 9B GGUF", "Qwen3.5 27B GGUF"], m.NotesProfile);
        var llamaExe = Field("llama.cpp · llama-cli.exe", m.LlamaExecutable, picker: true); var llamaModel = Field("Qwen GGUF 모델 파일", m.LlamaModel, picker: true);
        Section("오디오 변환 · 처리 방식", "CPU가 기본입니다. CUDA는 호환 실행 파일과 NVIDIA 장치 지원을 직접 확인한 경우에만 선택하세요.");
        var ffmpeg = Field("FFmpeg 네이티브 실행 파일", m.FfmpegExecutable, picker: true);
        var compute = Choice("처리 장치", ["CPU", "CUDA"], m.Compute);
        var cuda = new CheckBox { Content = NativeWhite.Text("이 실행 파일과 NVIDIA 장치의 CUDA 지원을 직접 확인했습니다. Vemo가 테스트한 것으로 간주하지 않습니다.", "secondary"), IsChecked = m.CudaConfirmed }; group.Children.Add(cuda);
        var layers = Field("GPU 레이어 수 · 부분 오프로딩", m.GpuLayers.ToString()); var timeout = Field("프로세스별 시간 제한 · 분", m.TimeoutMinutes.ToString());
        group.Children.Add(NativeWhite.Text("Q4 모델의 대략적인 가용 RAM: 9B 8–16 GB, 27B 20–32 GB. 문맥과 양자화에 따라 달라집니다. 27B가 16 GB VRAM에 모두 들어간다고 가정하지 마세요.", "caption"));
        content.Children.Add(NativeWhite.Text("현재 제한: 14,000자 초과 전사문의 노트 생성은 지원하지 않습니다. 전사는 유지됩니다. 자동 제목, 오디오 재생, 트레이 상주와 전원 조건은 이 프리뷰에 없습니다.", "caption"));
        void UpdateAvailability()
        {
            var paths = new[] { whisperExe.Text, whisperModel.Text, llamaExe.Text, llamaModel.Text, ffmpeg.Text }; var missing = paths.Count(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p));
            availability.Text = missing > 0 ? $"필수 파일 {missing}개를 선택하거나 경로를 확인하세요." : "선택한 파일이 존재합니다. 실행 호환성과 결과 품질은 실제 처리 시 확인하세요.";
            availability.Foreground = missing > 0 ? NativeWhite.Danger : NativeWhite.Secondary;
            var gpu = compute.SelectedItem?.ToString() == "CUDA"; cuda.IsEnabled = gpu; layers.IsEnabled = gpu;
        }
        foreach (var field in new[] { whisperExe, whisperModel, llamaExe, llamaModel, ffmpeg }) field.TextChanged += (_, _) => UpdateAvailability(); compute.SelectionChanged += (_, _) => UpdateAvailability(); UpdateAvailability();
        import.Click += async (_, _) =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "설치한 settings.json 선택", AllowMultiple = false });
                if (closed || files.Count == 0) return;
                var path = files[0].TryGetLocalPath() ?? throw new IOException("이 PC에 저장된 설정 파일을 선택하세요.");
                var preset = PresetSettings.Load(path); var selection = preset.Models;
                whisperProfile.SelectedItem = selection.WhisperProfile; notesProfile.SelectedItem = selection.NotesProfile;
                whisperExe.Text = selection.WhisperExecutable; whisperModel.Text = selection.WhisperModel;
                llamaExe.Text = selection.LlamaExecutable; llamaModel.Text = selection.LlamaModel; ffmpeg.Text = selection.FfmpegExecutable;
                compute.SelectedItem = "CPU"; cuda.IsChecked = false; layers.Text = selection.GpuLayers.ToString(); timeout.Text = selection.TimeoutMinutes.ToString();
                if (!string.IsNullOrWhiteSpace(preset.OutputDirectory)) output.Text = preset.OutputDirectory;
                message.Foreground = NativeWhite.Secondary; message.Text = "프리셋을 불러왔습니다. 경로와 저장 폴더를 확인한 뒤 저장하세요. 실행 파일을 시작하거나 모바일 연결을 가져오지 않았습니다."; UpdateAvailability();
            }
            catch (Exception e) { if (!closed) { message.Foreground = NativeWhite.Danger; message.Text = e.Message; } }
        };
        save.Click += (_, _) =>
        {
            try
            {
                if (closed) return;
                int Number(TextBox field, int minimum, int maximum, string label)
                {
                    if (int.TryParse(field.Text, out var value) && value >= minimum && value <= maximum) return value;
                    var error = fieldErrors[field]; error.Text = $"{minimum}–{maximum} 사이의 정수를 입력하세요."; error.IsVisible = true;
                    field.Focus(); field.SelectAll(); field.BringIntoView();
                    throw new InvalidDataException($"{label}: {error.Text}");
                }
                var gpuLayers = compute.SelectedItem?.ToString() == "CUDA" ? Number(layers, 0, 256, "GPU 레이어 수") : m.GpuLayers;
                var timeoutMinutes = Number(timeout, 1, 1440, "프로세스별 시간 제한 · 분");
                if (compute.SelectedItem?.ToString() == "CUDA" && cuda.IsChecked != true)
                {
                    cuda.Focus(); throw new InvalidDataException("CUDA 지원을 확인한 뒤 확인란을 선택하거나 CPU를 선택하세요.");
                }
                var updated = store.Settings with { OutputDirectory = output.Text?.Trim() ?? "", MobileDirectory = sync.Text?.Trim() ?? "", Models = new() { WhisperProfile = whisperProfile.SelectedItem?.ToString() ?? "large-v3-turbo", NotesProfile = notesProfile.SelectedItem?.ToString() ?? "Qwen3.5 9B GGUF", WhisperExecutable = whisperExe.Text?.Trim() ?? "", WhisperModel = whisperModel.Text?.Trim() ?? "", LlamaExecutable = llamaExe.Text?.Trim() ?? "", LlamaModel = llamaModel.Text?.Trim() ?? "", FfmpegExecutable = ffmpeg.Text?.Trim() ?? "", Compute = compute.SelectedItem?.ToString() ?? "CPU", CudaConfirmed = cuda.IsChecked == true, GpuLayers = gpuLayers, TimeoutMinutes = timeoutMinutes } };
                ApplySettings(store, mobile, updated); Close();
            }
            catch (Exception e) { if (!closed) { message.Foreground = NativeWhite.Danger; message.Text = e.Message; } }
        };
    }

    // Reject known ownership conflicts before committing; registration is not transactional.
    internal static void ApplySettings(LibraryStore store, MobileFolderAdapter mobile, AppSettings updated)
    {
        var output = string.IsNullOrWhiteSpace(updated.OutputDirectory) ? "" : SafeFiles.Full(updated.OutputDirectory);
        var sync = string.IsNullOrWhiteSpace(updated.MobileDirectory) ? "" : SafeFiles.Full(updated.MobileDirectory);
        updated = updated with { OutputDirectory = output, MobileDirectory = sync };
        if (output != "") SafeFiles.Disjoint(store.Root, output);
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
