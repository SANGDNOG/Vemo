using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Vemo.Core;
namespace Vemo.App;

public sealed partial class SettingsView : UserControl
{
    public event EventHandler? CloseRequested;
    bool closed, busy;
    Action? releaseGoogle;
    Control? initialFocus;
    readonly CancellationTokenSource lifetime = new();
    Task pending = Task.CompletedTask;
    CancellationTokenSource? operation;
    event Action? BusyChanged;
    public SettingsView(LibraryStore store, MobileFolderAdapter mobile, Action? close = null, GoogleConnectionService? google = null, bool traySupported = false)
    {
        Classes.Add("settings-view");
        CloseRequested += (_, _) => close?.Invoke();
        DetachedFromVisualTree += (_, _) => { closed = true; lifetime.Cancel(); releaseGoogle?.Invoke(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { RequestClose(); e.Handled = true; } };
        var s = store.Settings; var m = s.Models;
        var outer = new Grid { RowDefinitions = new("Auto,*,Auto") }; Content = outer;
        var header = new StackPanel { Margin = new Thickness(32, 0, 32, 0), Spacing = 0 };
        var topline = new Grid { ColumnDefinitions = new("*,Auto"), Height = 58 };
        var context = NativeWhite.Text("Vemo 환경 설정", "caption"); context.FontSize = 10; context.VerticalAlignment = VerticalAlignment.Center; topline.Children.Add(context);
        var dismiss = NativeWhite.Action("", RequestClose, "quiet");
        var closeCanvas = new Canvas { Width = 24, Height = 24 };
        closeCanvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse("M6,6 L18,18 M18,6 L6,18"), Stroke = NativeWhite.Secondary, StrokeThickness = 1.6, StrokeLineCap = Avalonia.Media.PenLineCap.Round, IsHitTestVisible = false });
        dismiss.Content = new Viewbox { Width = 19, Height = 19, Child = closeCanvas, IsHitTestVisible = false };
        dismiss.Width = 36; dismiss.Height = 36; dismiss.Padding = new Thickness(0); dismiss.VerticalAlignment = VerticalAlignment.Center;
        initialFocus = dismiss; NativeWhite.Label(dismiss, "설정 닫기"); ToolTip.SetTip(dismiss, "설정 닫기"); Grid.SetColumn(dismiss, 1); topline.Children.Add(dismiss);
        header.Children.Add(topline); var heading = NativeWhite.Text("설정", "heading"); heading.FontSize = 25; heading.Margin = new Thickness(0, 0, 0, 22); header.Children.Add(heading); outer.Children.Add(header);
        var content = new StackPanel { Margin = new Thickness(32, 25, 32, 30), Spacing = 25 }; var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); outer.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(32, 16, 32, 20), Spacing = 12 }; var message = NativeWhite.Text("", "secondary"); NativeWhite.Label(message, "설정 저장 상태"); footer.Children.Add(new ScrollViewer { Content = message, MaxHeight = 100 });
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; footerButtons.Children.Add(NativeWhite.Action("취소", RequestClose, "quiet")); var save = new Button { Content = "설정 저장" }; BusyChanged += () => save.IsEnabled = !busy; save.Classes.Add("primary"); footerButtons.Children.Add(save); footer.Children.Add(footerButtons);
        var footerBorder = new Border { Child = footer, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(footerBorder, 2); outer.Children.Add(footerBorder);
        StackPanel group = new();
        Border Card(Control child) => new() { Child = child, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Background = Avalonia.Media.Brushes.White, ClipToBounds = true };
        void AddRow(Control child)
        {
            group.Children.Add(new Border { Child = child, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, group.Children.Count > 0 ? 1 : 0, 0, 0) });
        }
        void Section(string name, string help)
        {
            var section = new StackPanel { Spacing = 10 }; var label = NativeWhite.Text(name, "secondary"); label.FontSize = 11; label.FontWeight = Avalonia.Media.FontWeight.Medium; section.Children.Add(label);
            group = new StackPanel();
            var card = Card(group); card.Classes.Add("settings-group"); section.Children.Add(card);
            if (help.Length > 0) { var note = NativeWhite.Text(help, "caption"); note.Margin = new Thickness(4, 0); section.Children.Add(note); }
            content.Children.Add(section);
        }
        void Row(string label, Control editor, string? help = null)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto"), MinHeight = 59, Margin = new Thickness(14, 0) };
            row.Classes.Add("settings-row");
            var text = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 12, 12) };
            var title = NativeWhite.Text(label); title.FontSize = 12; text.Children.Add(title);
            if (help != null) { var detail = NativeWhite.Text(help, "caption"); detail.FontSize = 10; detail.LineHeight = 15; text.Children.Add(detail); } row.Children.Add(text);
            if (editor is Button button) { button.FontSize = 11; button.Padding = new Thickness(14, 10); button.MinHeight = 36; }
            editor.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(editor, 1); row.Children.Add(editor); AddRow(row);
        }
        var fieldErrors = new Dictionary<TextBox, TextBlock>();
        TextBox Field(string label, string value, bool folder = false, bool picker = false)
        {
            var field = new StackPanel { Spacing = 6, Margin = new Thickness(14, 12) }; field.Classes.Add("settings-field"); AddRow(field);
            var caption = NativeWhite.Text(label); caption.FontSize = 12; var row = new Grid { ColumnDefinitions = new("*,Auto") }; row.Children.Add(caption);
            var box = new TextBox { Text = value }; NativeWhite.Label(box, label); AutomationProperties.SetLabeledBy(box, caption); ToolTip.SetTip(box, label);
            if (picker || folder)
            {
                box.Classes.Add("settings-path");
                var browse = new Button { Content = "변경", FontSize = 11, Padding = new Thickness(14, 10), MinHeight = 36, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; browse.Classes.Add("quiet"); NativeWhite.Label(browse, label + " 찾아보기"); Grid.SetColumn(browse, 1);
                BusyChanged += () => browse.IsEnabled = !busy;
                browse.Click += async (_, _) => await RunOperation(async token =>
                {
                        var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("설정 창을 연 뒤 다시 시도하세요.");
                        var selected = folder ? (IReadOnlyList<IStorageItem>)await provider.OpenFolderPickerAsync(new() { Title = label, AllowMultiple = false }) : await provider.OpenFilePickerAsync(new() { Title = label, AllowMultiple = false });
                        if (!closed && !token.IsCancellationRequested && selected.Count > 0) box.Text = selected[0].TryGetLocalPath() ?? throw new IOException("이 PC의 로컬 경로를 선택하세요.");
                }, message);
                row.Children.Add(browse);
            }
            if (folder)
            {
                field.Margin = new Thickness(14, 12); field.MinHeight = 35; field.Spacing = 0;
                row.Children.Remove(caption);
                var labels = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(caption); labels.Children.Add(box); row.Children.Add(labels);
                box.MinHeight = 15; box.FontSize = 10; box.Foreground = NativeWhite.Secondary; box.Background = Avalonia.Media.Brushes.Transparent; box.BorderThickness = new Thickness(0); box.Padding = new Thickness(0); box.PlaceholderText = "폴더를 선택하세요";
                row.MinHeight = 35;
            }
            field.Children.Add(row); if (!folder) field.Children.Add(box);
            var error = NativeWhite.Text("", "error"); error.IsVisible = false; field.Children.Add(error); fieldErrors.Add(box, error);
            box.TextChanged += (_, _) => { error.IsVisible = false; }; return box;
        }
        ComboBox Choice(string label, string[] options, string selected)
        {
            var box = new ComboBox { ItemsSource = options.Contains(selected) ? options : options.Append(selected).ToArray(), SelectedItem = selected, FontSize = 11, Padding = new Thickness(7), MinWidth = 125, MaxWidth = 140, HorizontalAlignment = HorizontalAlignment.Right }; NativeWhite.Label(box, label); Row(label, box); return box;
        }
        var googleSection = BuildGoogleSettings(google ?? GoogleConnectionService.Shared(store), message, store.ProcessorId);
        if (googleSection is StackPanel googleStack && googleStack.Children.OfType<Border>().FirstOrDefault() is { } googleCard)
        { googleCard.BorderBrush = NativeWhite.Divider; googleCard.BorderThickness = new Thickness(1); googleCard.CornerRadius = new CornerRadius(13); googleCard.Background = Avalonia.Media.Brushes.White; googleCard.ClipToBounds = true; }
        Section("입력 · 출력 폴더", "");
        var input = Field("오디오 입력 폴더", s.InputDirectory, true);
        var output = Field("자동 ZIP 저장 폴더", s.OutputDirectory, true);
        content.Children.Add(googleSection);
        var legacyFields = new StackPanel(); group = legacyFields;
        var sync = Field("모바일 동기화 폴더", s.MobileDirectory, true);
        var scanLocal = new Button { Content = "다시 확인" }; scanLocal.Classes.Add("quiet"); Row("로컬 동기화 상태", scanLocal);
        BusyChanged += () => scanLocal.IsEnabled = !busy;
        scanLocal.Click += async (_, _) => await RunOperation(async token =>
        {
            if ((sync.Text?.Trim() ?? "") != store.Settings.MobileDirectory) throw new InvalidOperationException("변경한 동기화 폴더를 먼저 저장하세요.");
            if (string.IsNullOrWhiteSpace(store.Settings.MobileDirectory)) throw new InvalidOperationException("모바일 동기화 폴더를 선택하고 저장하세요.");
            var result = await Task.Run(() => mobile.ScanAsync(token), token);
            if (!closed) { message.Foreground = result.Issues.Count > 0 ? NativeWhite.Danger : NativeWhite.Secondary; message.Text = $"{result.Imported}개 가져옴 · {result.Skipped}개 건너뜀. " + string.Join("; ", result.Issues.Select(i => i.Message).Take(3)); }
        }, message);
        Section("ZIP 아카이브", "");
        var archiveSection = content.Children[^1];
        var autoExport = new ToggleSwitch { OnContent = null, OffContent = null, IsChecked = s.AutoExport }; NativeWhite.Label(autoExport, "완료된 녹음 자동 ZIP 저장"); Row("완료된 녹음 자동 ZIP 저장", autoExport);
        Section("모델", "");
        var whisperProfile = Choice("전사 모델", ["small", "large-v3-turbo"], m.WhisperProfile);
        var notesProfile = Choice("노트 모델", ["Qwen3.5 9B GGUF", "Qwen3.5 27B GGUF"], m.NotesProfile);
        var import = new Button { Content = "프리셋 설정 가져오기…" }; import.Classes.Add("quiet");
        var availability = NativeWhite.Text("", "caption"); availability.Margin = new Thickness(16, 12); group.Children.Add(availability);
        var modelGroup = group;
        var advancedFields = new StackPanel(); group = advancedFields;
        Row("설치한 모델 설정", import);
        var whisperExe = Field("whisper.cpp · whisper-cli.exe", m.WhisperExecutable, picker: true); var whisperModel = Field("Whisper ggml 모델 파일", m.WhisperModel, picker: true);
        var llamaExe = Field("llama.cpp · llama-cli.exe", m.LlamaExecutable, picker: true); var llamaModel = Field("Qwen GGUF 모델 파일", m.LlamaModel, picker: true);
        var ffmpeg = Field("FFmpeg 네이티브 실행 파일", m.FfmpegExecutable, picker: true);
        var advanced = new Expander { Header = "고급 · 실행 파일과 모델 경로", Content = advancedFields, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch }; advanced.Classes.Add("settings-advanced"); modelGroup.Children.Add(advanced);
        Section("처리", "");
        var externalPower = new ToggleSwitch { OnContent = null, OffContent = null, IsChecked = s.RequireExternalPower }; NativeWhite.Label(externalPower, "전원 연결 시에만 처리"); Row("전원 연결 시에만 처리", externalPower); ToolTip.SetTip(externalPower, "배터리 사용 중에는 새 작업을 기다립니다.");
        var automatic = new ToggleSwitch { OnContent = null, OffContent = null, IsChecked = !s.QueuePaused }; NativeWhite.Label(automatic, "새 작업 자동 처리"); Row("새 작업 자동 처리", automatic); ToolTip.SetTip(automatic, "진행 중인 작업과 기존 결과는 유지합니다.");
        var processingGroup = group; group = advancedFields;
        var compute = Choice("처리 장치", ["CPU", "CUDA"], m.Compute);
        var cuda = new CheckBox { Content = NativeWhite.Text("CUDA 실행 파일과 NVIDIA 장치 지원을 확인했습니다.", "secondary"), IsChecked = m.CudaConfirmed, Margin = new Thickness(16, 12) }; group.Children.Add(cuda);
        var layers = Field("GPU 레이어 수 · 부분 오프로딩", m.GpuLayers.ToString()); var timeout = Field("프로세스별 시간 제한 · 분", m.TimeoutMinutes.ToString());
        group = processingGroup;
        content.Children.Remove(archiveSection); content.Children.Add(archiveSection);
        Section("시작 · 창 닫기", "");
        var startupInitial = OperatingSystem.IsWindows() && WindowsStartup.IsEnabled;
        var startup = new ToggleSwitch { OnContent = null, OffContent = null, IsChecked = startupInitial, IsVisible = OperatingSystem.IsWindows() };
        NativeWhite.Label(startup, "Windows 로그인 시 실행");
        if (OperatingSystem.IsWindows()) Row("Windows 로그인 시 실행", startup);
        var closeToTray = new ToggleSwitch { OnContent = null, OffContent = null, IsChecked = s.CloseToTray, IsEnabled = traySupported };
        NativeWhite.Label(closeToTray, "창을 닫아도 트레이에 유지");
        Row("창을 닫아도 트레이에 유지", closeToTray, traySupported ? "트레이 메뉴의 ‘종료’로 앱을 끝낼 수 있습니다." : "이 환경에서는 Windows 트레이를 사용할 수 없습니다.");
        var legacy = new Expander { Header = "고급 · 모바일 폴더", Content = legacyFields, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        legacy.Classes.Add("settings-advanced"); content.Children.Add(legacy);

        void UpdateAvailability()
        {
            var paths = new[] { whisperExe.Text, whisperModel.Text, llamaExe.Text, llamaModel.Text, ffmpeg.Text }; var missing = paths.Count(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p));
            availability.Text = missing > 0 ? $"필수 파일 {missing}개를 선택하거나 경로를 확인하세요." : "모델 파일을 확인했습니다.";
            availability.Foreground = missing > 0 ? NativeWhite.Danger : NativeWhite.Secondary;
            var gpu = compute.SelectedItem?.ToString() == "CUDA"; cuda.IsEnabled = gpu; layers.IsEnabled = gpu;
        }
        foreach (var field in new[] { whisperExe, whisperModel, llamaExe, llamaModel, ffmpeg }) field.TextChanged += (_, _) => UpdateAvailability(); compute.SelectionChanged += (_, _) => UpdateAvailability(); UpdateAvailability();
        BusyChanged += () => import.IsEnabled = !busy;
        import.Click += async (_, _) => await RunOperation(async token =>
        {
                var files = await (TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("설정 창을 연 뒤 다시 시도하세요.")).OpenFilePickerAsync(new() { Title = "설치한 settings.json 선택", AllowMultiple = false });
                if (closed || token.IsCancellationRequested || files.Count == 0) return;
                var path = files[0].TryGetLocalPath() ?? throw new IOException("이 PC에 저장된 설정 파일을 선택하세요.");
                var preset = PresetSettings.Load(path); var selection = preset.Models;
                whisperProfile.SelectedItem = selection.WhisperProfile; notesProfile.SelectedItem = selection.NotesProfile;
                whisperExe.Text = selection.WhisperExecutable; whisperModel.Text = selection.WhisperModel;
                llamaExe.Text = selection.LlamaExecutable; llamaModel.Text = selection.LlamaModel; ffmpeg.Text = selection.FfmpegExecutable;
                compute.SelectedItem = "CPU"; cuda.IsChecked = false; layers.Text = selection.GpuLayers.ToString(); timeout.Text = selection.TimeoutMinutes.ToString();
                if (!string.IsNullOrWhiteSpace(preset.OutputDirectory)) output.Text = preset.OutputDirectory;
                message.Foreground = NativeWhite.Secondary; message.Text = "프리셋을 불러왔습니다. 경로를 확인하고 저장하세요."; UpdateAvailability();
        }, message);
        save.Click += (_, _) =>
        {
            try
            {
                if (closed || busy) return;
                int Number(TextBox field, int minimum, int maximum, string label)
                {
                    if (int.TryParse(field.Text, out var value) && value >= minimum && value <= maximum) return value;
                    advanced.IsExpanded = true;
                    var error = fieldErrors[field]; error.Text = $"{minimum}–{maximum} 사이의 정수를 입력하세요."; error.IsVisible = true;
                    field.Focus(); field.SelectAll(); field.BringIntoView();
                    throw new InvalidDataException($"{label}: {error.Text}");
                }
                var gpuLayers = compute.SelectedItem?.ToString() == "CUDA" ? Number(layers, 0, 256, "GPU 레이어 수") : m.GpuLayers;
                var timeoutMinutes = Number(timeout, 1, 1440, "프로세스별 시간 제한 · 분");
                if (compute.SelectedItem?.ToString() == "CUDA" && cuda.IsChecked != true)
                {
                    advanced.IsExpanded = true; cuda.Focus(); throw new InvalidDataException("CUDA 지원을 확인한 뒤 확인란을 선택하거나 CPU를 선택하세요.");
                }
                var updated = store.Settings with { InputDirectory = input.Text?.Trim() ?? "", AutoExport = autoExport.IsChecked == true, RequireExternalPower = externalPower.IsChecked == true, CloseToTray = traySupported ? closeToTray.IsChecked == true : store.Settings.CloseToTray, QueuePaused = automatic.IsChecked != true, OutputDirectory = output.Text?.Trim() ?? "", MobileDirectory = sync.Text?.Trim() ?? "", Models = store.Settings.Models with { WhisperProfile = whisperProfile.SelectedItem?.ToString() ?? "large-v3-turbo", NotesProfile = notesProfile.SelectedItem?.ToString() ?? "Qwen3.5 9B GGUF", WhisperExecutable = whisperExe.Text?.Trim() ?? "", WhisperModel = whisperModel.Text?.Trim() ?? "", LlamaExecutable = llamaExe.Text?.Trim() ?? "", LlamaModel = llamaModel.Text?.Trim() ?? "", FfmpegExecutable = ffmpeg.Text?.Trim() ?? "", Compute = compute.SelectedItem?.ToString() ?? "CPU", CudaConfirmed = cuda.IsChecked == true, GpuLayers = gpuLayers, TimeoutMinutes = timeoutMinutes } };
                var changeStartup = OperatingSystem.IsWindows() && (startup.IsChecked == true) != startupInitial;
                var previousStartup = false;
                if (changeStartup)
                {
                    previousStartup = WindowsStartup.IsEnabled;
                    if (WindowsStartup.Error != null) throw new IOException(WindowsStartup.Error);
                    if (!WindowsStartup.SetEnabled(startup.IsChecked == true)) throw new IOException(WindowsStartup.Error ?? "시작 프로그램 설정을 변경하지 못했습니다.");
                }
                try { SettingsWindow.ApplySettings(store, mobile, updated); }
                catch (Exception error)
                {
                    if (changeStartup && !WindowsStartup.SetEnabled(previousStartup))
                        throw new IOException(error.Message + " · 시작 프로그램 복원 실패: " + WindowsStartup.Error, error);
                    throw;
                }
                RequestClose();
            }
            catch (Exception e) { if (!closed) { message.Foreground = NativeWhite.Danger; message.Text = e.Message; } }
        };
    }

    async Task RunOperation(Func<CancellationToken, Task> action, TextBlock message)
    {
        if (closed || busy) return;
        busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); BusyChanged?.Invoke();
        pending = Run();
        await pending;
        async Task Run()
        {
            try { await action(operation.Token); }
            catch (OperationCanceledException) { if (!closed) { message.Foreground = NativeWhite.Secondary; message.Text = "작업을 취소했습니다."; } }
            catch (Exception error) { if (!closed) { message.Foreground = NativeWhite.Danger; message.Text = error.Message; } }
            finally { operation?.Dispose(); operation = null; busy = false; if (!closed) BusyChanged?.Invoke(); }
        }
    }
    public void FocusFirstControl()
    {
        if (!closed && initialFocus is { IsEnabled: true, IsVisible: true }) initialFocus.Focus();
    }
    public async Task StopAsync()
    {
        closed = true; lifetime.Cancel(); releaseGoogle?.Invoke();
        await pending;
    }
    void RequestClose()
    {
        if (closed) return;
        closed = true; lifetime.Cancel(); releaseGoogle?.Invoke();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
