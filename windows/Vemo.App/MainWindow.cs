using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Vemo.Core;
using System.Globalization;
namespace Vemo.App;

public sealed class MainWindow : Window
{
    readonly LibraryStore store; readonly JobRunner runner; readonly MobileFolderAdapter mobile;
    readonly ListBox navigation = new(), folders = new(), jobs = new() { SelectionMode = SelectionMode.Multiple };
    readonly TextBox search = new() { PlaceholderText = "제목 · 전사 · 노트 검색" }, title = new(), date = new() { PlaceholderText = "ISO 8601 날짜와 시간대 · 모르면 비워 두세요" };
    readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }, notes = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock status = NativeWhite.Text("", "secondary"), jobInfo = NativeWhite.Text("", "secondary"), queueInfo = NativeWhite.Text("", "secondary"), listTitle = NativeWhite.Text("전체 녹음"), listCount = NativeWhite.Text("", "secondary"), selectionInfo = NativeWhite.Text("", "caption"), detailPath = NativeWhite.Text("", "secondary"), detailState = NativeWhite.Text("", "secondary");
    readonly Button emptyImport;
    readonly TextBlock emptyDetailTitle = NativeWhite.Text("", "heading"), emptyDetailHelp = NativeWhite.Text("", "secondary");
    readonly StackPanel renderedNotes = new() { Spacing = 12, Margin = new Thickness(0, 16) };
    readonly Border stateBanner = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 10, 0, 12) };
    readonly TabControl tabs = new(); readonly Grid detailBody = new(); readonly StackPanel emptyDetail = new(); readonly TextBlock emptyList = NativeWhite.Text("", "secondary");
    readonly WindowWork work = new(); readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<(Control Control, Func<bool> Enabled)> actions = [];
    readonly Dictionary<string, (string Signature, string Text)> searchCache = [];
    readonly HashSet<string> collapsedFolders = [];
    string? selectedFolder, detailId; string view = "all"; bool refreshing, closing, closeReady, editing, importing; Task backgroundTask = Task.CompletedTask; string lastSignature = ""; int focusPane;
    record FolderChoice(string? Id, string Label, string View = "folder", int Depth = 0, int Count = 0, bool HasChildren = false, bool Expanded = true) { public override string ToString() => Label; }
    record JobChoice(RecordingJob Job) { public override string ToString() => $"{Job.Title} · {StateLabel(Job.State)}"; }
    public MainWindow()
    {
        Title = "Vemo — Windows functional preview"; Width = 1280; Height = 820; MinWidth = 980; MinHeight = 540;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Vemo.App/Assets/vemo-icon.png")));
        store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vemo", "WindowsPreview")); runner = new(store, new LocalCliInferenceEngine()); mobile = new(store);
        navigation.Classes.Add("navigation"); folders.Classes.Add("navigation"); jobs.Classes.Add("recordings"); search.Classes.Add("search"); title.Classes.Add("title"); transcript.Classes.Add("reading"); notes.Classes.Add("reading");
        NativeWhite.Label(navigation, "라이브러리", "방향키로 전체 녹음, 미분류, 휴지통 선택"); NativeWhite.Label(folders, "폴더", "위/아래 선택, 왼쪽 접기/상위 폴더, 오른쪽 펼치기, F6 다음 영역"); NativeWhite.Label(jobs, "녹음 목록", "Ctrl/Shift 다중 선택, Ctrl+A 전체 선택, Delete 휴지통");
        NativeWhite.Label(search, "현재 목록의 제목, 전사, 노트 검색", "Ctrl+F 검색, Escape 지우기"); NativeWhite.Label(title, "녹음 제목", "Enter 저장, Escape 변경 취소"); NativeWhite.Label(date, "녹음 날짜", "시간대를 포함한 ISO 8601, 모르면 비워 두세요"); NativeWhite.Label(transcript, "전사 원문"); NativeWhite.Label(notes, "생성된 노트, Markdown 원문");
        folders.ItemTemplate = new FuncDataTemplate<FolderChoice>((f, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("20,Auto,*,Auto,Auto"), Margin = new Thickness(f!.Depth * 12, 0, 0, 0) }; row.Children.Add(NativeWhite.SelectionMark());
            var label = new TextBlock { Text = f.Label, TextTrimming = TextTrimming.CharacterEllipsis }; ToolTip.SetTip(label, f.Label); Grid.SetColumn(label, 2); row.Children.Add(label);
            var count = NativeWhite.Text(f.Count.ToString(), "caption"); Grid.SetColumn(count, 3); row.Children.Add(count);
            if (f.HasChildren)
            {
                var toggle = NativeWhite.Action(f.Expanded ? "⌄" : "›", () => ToggleFolder(f.Id!)); toggle.Classes.Add("tree-toggle");
                NativeWhite.Label(toggle, f.Label + (f.Expanded ? " 접기" : " 펼치기"), "폴더 목록에서 왼쪽/오른쪽 화살표로도 접고 펼칠 수 있습니다.");
                Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
            }
            if (f.Id != null)
            {
                Control[] FolderActions() => [Menu("하위 폴더 만들기…", () => CreateFolder(f.Id)), Menu("폴더 이동…", () => MoveFolder(f.Id))];
                row.ContextMenu = new ContextMenu { ItemsSource = FolderActions() };
                var manage = new Button { Content = "⋯", Flyout = new MenuFlyout { ItemsSource = FolderActions() } }; manage.Classes.Add("tree-toggle");
                NativeWhite.Label(manage, f.Label + " 폴더 관리"); Grid.SetColumn(manage, 4); row.Children.Add(manage);
            }
            NativeWhite.Label(row, $"{f.Label}, {f.Count}개" + (f.HasChildren ? f.Expanded ? ", 펼쳐짐" : ", 접힘" : ""));
            return row;
        });
        navigation.ItemTemplate = folders.ItemTemplate;
        jobs.ItemTemplate = new FuncDataTemplate<JobChoice>((choice, _) =>
        {
            var j = choice!.Job; var row = new StackPanel { Spacing = 7 };
            row.Children.Add(new TextBlock { Text = j.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            var recorded = NativeWhite.Text(DisplayDate(j.RecordedAt), "caption"); ToolTip.SetTip(recorded, j.RecordedAt ?? "녹음 날짜 미확인"); row.Children.Add(recorded);
            row.Children.Add(NativeWhite.Text((IsPendingPurge(j) ? "영구 삭제 미완료 · 삭제 재시도 필요" : StateLabel(j.State)) + (j.ExportError != null ? " · ZIP 저장 실패" : ""), j.State == JobState.Failed || j.ExportError != null ? "error" : "caption"));
            ToolTip.SetTip(row, j.Title + "\n" + (j.Error ?? j.ExportError ?? StateLabel(j.State)));
            var selectedRow = new Grid { ColumnDefinitions = new("*,20") }; selectedRow.Children.Add(row); var mark = NativeWhite.SelectionMark(); Grid.SetColumn(mark, 1); selectedRow.Children.Add(mark); return selectedRow;
        });
        var outer = new Grid { RowDefinitions = new("*,Auto") };
        var columns = new Grid { ColumnDefinitions = new("220,310,*") }; outer.Children.Add(columns);
        var sidebar = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), Background = NativeWhite.Sidebar };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(22, 26, 20, 28) };
        brand.Children.Add(new Image { Width = 28, Height = 28, Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Vemo.App/Assets/vemo-icon.png"))) }); brand.Children.Add(new TextBlock { Text = "Vemo", FontSize = 23, FontWeight = FontWeight.SemiBold }); sidebar.Children.Add(brand);
        var libraryHeading = NativeWhite.Text("라이브러리", "caption"); libraryHeading.Margin = new Thickness(22, 0, 12, 8); Grid.SetRow(libraryHeading, 1); sidebar.Children.Add(libraryHeading);
        Grid.SetRow(navigation, 2); sidebar.Children.Add(navigation);
        var folderHeading = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(20, 20, 12, 10) }; folderHeading.Children.Add(NativeWhite.Text("폴더", "caption"));
        var create = Button("+ 폴더", CreateFolder, "soft"); Grid.SetColumn(create, 1); folderHeading.Children.Add(create); Grid.SetRow(folderHeading, 3); sidebar.Children.Add(folderHeading);
        Grid.SetRow(folders, 4); sidebar.Children.Add(folders);
        var sidebarBottom = new StackPanel { Margin = new Thickness(12), Spacing = 6 };
        sidebarBottom.Children.Add(Button("설정", OpenSettings, "quiet")); sidebarBottom.Children.Add(Button("선택 폴더 이동…", MoveFolder, "quiet", () => selectedFolder != null)); Grid.SetRow(sidebarBottom, 5); sidebar.Children.Add(sidebarBottom);
        columns.Children.Add(NativeWhite.Panel(sidebar, true));
        var listPane = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        var listHeader = new StackPanel { Margin = new Thickness(20, 24, 20, 12), Spacing = 18 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto") }; listTitle.FontSize = 18; listTitle.FontWeight = FontWeight.SemiBold; heading.Children.Add(listTitle); Grid.SetColumn(listCount, 1); heading.Children.Add(listCount); listHeader.Children.Add(heading); listHeader.Children.Add(search); listPane.Children.Add(listHeader);
        var selection = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(20, 0, 12, 8) }; selection.Children.Add(selectionInfo);
        var menuButton = new Button { Content = "선택 작업 ⋯", Flyout = new MenuFlyout { ItemsSource = SelectionMenu() } }; menuButton.Classes.Add("quiet"); Grid.SetColumn(menuButton, 1); selection.Children.Add(menuButton); Grid.SetRow(selection, 1); listPane.Children.Add(selection);
        var listArea = new Grid(); listArea.Children.Add(jobs); emptyList.Margin = new Thickness(24); emptyList.VerticalAlignment = VerticalAlignment.Center; emptyList.TextAlignment = TextAlignment.Center; listArea.Children.Add(emptyList); Grid.SetRow(listArea, 2); listPane.Children.Add(listArea);
        DragDrop.SetAllowDrop(listPane, true);
        DragDrop.AddDragOverHandler(listPane, (_, e) => { e.DragEffects = !closing && !importing && DroppedFiles(e).Any(IsAudioFile) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        DragDrop.AddDropHandler(listPane, async (_, e) =>
        {
            e.Handled = true; var files = DroppedFiles(e).ToArray();
            if (closing || importing || files.Length == 0) return;
            await Execute(() => ImportFiles(() => Task.FromResult<IEnumerable<IStorageItem>>(files)));
        });
        jobs.ContextMenu = new ContextMenu { ItemsSource = SelectionMenu() };
        var import = Button("+  오디오 파일 가져오기", Import, "quiet", () => !importing); ToolTip.SetTip(import, "Ctrl+O · 녹음이 끝난 파일을 선택하거나 목록에 끌어 놓으세요"); import.HorizontalAlignment = HorizontalAlignment.Stretch; import.Margin = new Thickness(12); Grid.SetRow(import, 3); listPane.Children.Add(import);
        var listBorder = NativeWhite.Panel(listPane, true); Grid.SetColumn(listBorder, 1); columns.Children.Add(listBorder);
        var detail = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(32, 18, 32, 20) }; Grid.SetColumn(detail, 2); columns.Children.Add(detail);
        var top = new Grid { ColumnDefinitions = new("*,Auto") }; queueInfo.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(queueInfo);
        var queueMenu = new Button { Content = "처리 ⋯", Flyout = new MenuFlyout { ItemsSource = new Control[] { Menu("새 작업 일시중지 / 재개", ToggleQueue), Menu("모바일 폴더 지금 확인", Scan), Menu("설정", OpenSettings) } } }; queueMenu.Classes.Add("quiet"); Grid.SetColumn(queueMenu, 1); top.Children.Add(queueMenu); detail.Children.Add(top);
        var detailArea = new Grid();
        var detailScroll = new ScrollViewer { Content = detailArea, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; Grid.SetRow(detailScroll, 1); detail.Children.Add(detailScroll);
        detailArea.MinHeight = 440; transcript.MinHeight = 180; notes.MinHeight = 180; transcript.MaxHeight = 560; notes.MaxHeight = 560;
        emptyDetail.Focusable = true; emptyDetail.Spacing = 12; emptyDetail.VerticalAlignment = VerticalAlignment.Center; emptyDetail.HorizontalAlignment = HorizontalAlignment.Center; emptyDetail.MaxWidth = 350;
        emptyDetail.Children.Add(emptyDetailTitle); emptyDetail.Children.Add(emptyDetailHelp); emptyImport = Button("오디오 가져오기", Import, "primary", () => !importing); emptyDetail.Children.Add(emptyImport); detailArea.Children.Add(emptyDetail);
        detailBody.RowDefinitions = new("Auto,Auto,Auto,*,Auto"); detailArea.Children.Add(detailBody);
        var titleRow = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 24, 0, 4) }; titleRow.Children.Add(title);
        var more = new Button { Content = "⋯", Flyout = new MenuFlyout { ItemsSource = SelectionMenu() } }; NativeWhite.Label(more, "녹음 작업 메뉴"); more.Classes.Add("quiet"); Grid.SetColumn(more, 1); titleRow.Children.Add(more); detailBody.Children.Add(titleRow);
        var metadata = new StackPanel { Spacing = 8 }; metadata.Children.Add(detailPath); var edit = new Grid { ColumnDefinitions = new("*,Auto") }; edit.Children.Add(date); var save = Button("저장", SaveTitle, "soft", () => Current() is { State: not JobState.Purged }); Grid.SetColumn(save, 1); edit.Children.Add(save); metadata.Children.Add(edit); metadata.Children.Add(detailState); Grid.SetRow(metadata, 1); detailBody.Children.Add(metadata);
        stateBanner.Child = new ScrollViewer { Content = jobInfo, MaxHeight = 140 }; Grid.SetRow(stateBanner, 2); detailBody.Children.Add(stateBanner);
        tabs.ItemsSource = new[] { new TabItem { Header = "전사", Content = transcript }, new TabItem { Header = "노트", Content = new ScrollViewer { Content = renderedNotes, MaxHeight = 560, MinHeight = 180, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } } }; Grid.SetRow(tabs, 3); detailBody.Children.Add(tabs);
        var footer = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; footer.Children.Add(Button("복사", CopyText, null, HasResultText));
        var textExport = new Button { Content = "텍스트 내보내기", Flyout = new MenuFlyout { ItemsSource = new Control[] { Menu("전사 TXT…", () => ExportText(false, false), () => HasText(false)), Menu("전사 MD…", () => ExportText(false, true), () => HasText(false)), Menu("노트 MD…", () => ExportText(true, true), () => HasText(true)) } } }; footer.Children.Add(textExport);
        footer.Children.Add(Button("결과 폴더", OpenResults, null, () => Current() is { State: not JobState.Purged, ExportPath: { } path } && File.Exists(path)));
        footer.Children.Add(Button("ZIP 내보내기", Export, null, () => HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State == JobState.Completed))); footer.Children.Add(Button("선택 이동…", Move, null, HasSelection)); Grid.SetRow(footer, 4); detailBody.Children.Add(footer);
        status.Margin = new Thickness(16, 8); var statusBar = new Border { Child = new ScrollViewer { Content = status, MaxHeight = 70 }, Background = NativeWhite.Sidebar, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(statusBar, 1); outer.Children.Add(statusBar); Content = outer;
        navigation.SelectionChanged += (_, _) => { if (refreshing || closing) return; if (navigation.SelectedItem is FolderChoice f) { selectedFolder = null; view = f.View; lastSignature = ""; Refresh(); } };
        folders.SelectionChanged += (_, _) => { if (refreshing || closing) return; if (folders.SelectedItem is FolderChoice f) { selectedFolder = f.Id; view = f.View; } lastSignature = ""; Refresh(); };
        tabs.SelectionChanged += (_, _) => UpdateActions();
        jobs.SelectionChanged += (_, _) => { if (!refreshing) Detail(); }; search.TextChanged += (_, _) => { if (closing) return; lastSignature = ""; Refresh(); };
        title.TextChanged += (_, _) => { if (!refreshing) editing = true; }; date.TextChanged += (_, _) => { if (!refreshing) editing = true; };
        title.KeyDown += EditKey; date.KeyDown += EditKey; AddHandler(KeyDownEvent, Shortcut, RoutingStrategies.Tunnel);
        refresh.Tick += (_, _) => Refresh(); Opened += (_, _) => { if (closing) return; Refresh(); refresh.Start(); backgroundTask = work.Start(RunBackground); };
        Closing += (_, e) =>
        {
            if (closeReady) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; refresh.Stop();
            if (Content is Control content) content.IsEnabled = false;
            status.Text = "진행 중인 작업을 취소하고 저장 작업이 끝날 때까지 기다립니다…";
            _ = CloseAfterDrain();
        };
        Closed += (_, _) => { store.Dispose(); work.Dispose(); };
    }
    async Task CloseAfterDrain()
    {
        var drained = work.StopAsync(); runner.CancelActive(); foreach (var owned in OwnedWindows.ToArray()) owned.Close();
        try { await drained; } catch (Exception e) { status.Text = "Background stopped: " + e.Message; }
        try { await backgroundTask; } catch (OperationCanceledException) { } catch (Exception e) { status.Text = "Background stopped: " + e.Message; }
        closeReady = true; Close();
    }
    async Task Execute(Func<Task> action)
    {
        if (closing) return;
        try { await work.Start(_ => action()); if (!closing) { lastSignature = ""; Refresh(); } }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!closing) status.Text = e.Message; }
    }
    Button Button(string text, Func<Task> action, string? style = null, Func<bool>? enabled = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) }; if (style != null) b.Classes.Add(style);
        b.Click += async (_, _) => await Execute(action); if (enabled != null) actions.Add((b, enabled)); return b;
    }
    MenuItem Menu(string text, Func<Task> action, Func<bool>? enabled = null, string? gesture = null, bool danger = false)
    {
        var item = new MenuItem { Header = text }; if (gesture != null) item.InputGesture = KeyGesture.Parse(gesture); if (danger) item.Classes.Add("danger");
        item.Click += async (_, _) => await Execute(action); if (enabled != null) actions.Add((item, enabled)); return item;
    }
    Control[] SelectionMenu() => [Menu("제목 수정", EditTitle, () => HasSelection() && Selected().Count() == 1, "F2"), new Separator(), Menu("선택 이동…", Move, HasSelection), Menu("휴지통으로 이동", () => Action(ids => store.Trash(ids)), () => HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State is not (JobState.Transcribing or JobState.GeneratingNotes)), "Delete", true), Menu("복원", () => Action(ids => store.Restore(ids)), () => HasSelection() && SelectedJobs().All(j => j.Trashed)), Menu("소유 사본 영구 삭제…", Purge, () => Selected().Any() && SelectedJobs().All(j => j.Trashed && j.State is not (JobState.Transcribing or JobState.GeneratingNotes)), danger: true), new Separator(), Menu("작업 취소", Cancel, () => HasSelection() && SelectedJobs().Any(j => j.State is JobState.Queued or JobState.Transcribing or JobState.GeneratingNotes)), Menu("기존 모델 설정으로 재시도", () => Action(ids => store.Retry(ids)), Retryable), Menu("현재 모델 설정으로 재시도", () => Action(ids => store.Retry(ids, true)), Retryable), new Separator(), Menu("ZIP 내보내기", Export, () => HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State == JobState.Completed)), Menu("모바일 결과 게시", Publish, () => HasSelection() && SelectedJobs().All(j => j.State == JobState.Completed && j.Mobile != null))];
    static bool IsPendingPurge(RecordingJob job) => job.State == JobState.Purged && job.Error != null;
    bool HasSelection() => Selected().Any() && SelectedJobs().All(j => j.State != JobState.Purged);
    bool Retryable() => HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State is JobState.Failed or JobState.Cancelled);
    IEnumerable<RecordingJob> SelectedJobs() => Selected().Select(store.Get);
    Task OpenSettings() => new SettingsWindow(store, mobile).ShowDialog(this);
    Task ToggleQueue() { store.UpdateSettings(store.Settings with { QueuePaused = !store.Settings.QueuePaused }); return Task.CompletedTask; }
    void EditKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = Execute(SaveTitle); }
        else if (e.Key == Key.Escape) { editing = false; detailId = null; Detail(); e.Handled = true; }
    }
    void Shortcut(object? sender, KeyEventArgs e)
    {
        if (closing) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F) { search.Focus(); search.SelectAll(); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.O) { _ = Execute(Import); e.Handled = true; }
        else if (e.Key == Key.F6) { focusPane = navigation.IsKeyboardFocusWithin || folders.IsKeyboardFocusWithin ? 0 : jobs.IsKeyboardFocusWithin || search.IsKeyboardFocusWithin ? 1 : 2; focusPane = (focusPane + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 2 : 1)) % 3; (focusPane == 0 ? (Control)(view == "folder" ? folders : navigation) : focusPane == 1 ? search : detailBody.IsVisible ? title : view == "trash" ? emptyDetail : emptyImport).Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape && search.IsKeyboardFocusWithin) { search.Text = ""; jobs.Focus(); e.Handled = true; }
        else if (folders.IsKeyboardFocusWithin && folders.SelectedItem is FolderChoice folder && (e.Key is Key.Left or Key.Right))
        {
            if ((e.Key == Key.Right && folder.HasChildren && !folder.Expanded) || (e.Key == Key.Left && folder.HasChildren && folder.Expanded)) ToggleFolder(folder.Id!);
            else if (e.Key == Key.Left && store.Folders.FirstOrDefault(f => f.Id == folder.Id)?.ParentId is { } parent) { selectedFolder = parent; lastSignature = ""; Refresh(); }
            e.Handled = true;
        }
        else if (jobs.IsKeyboardFocusWithin && (e.Key is Key.Enter or Key.F2) && Current() != null) { _ = EditTitle(); e.Handled = true; }
        else if (jobs.IsKeyboardFocusWithin && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A) { jobs.SelectAll(); e.Handled = true; }
        else if (jobs.IsKeyboardFocusWithin && e.Key == Key.Delete && HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State is not (JobState.Transcribing or JobState.GeneratingNotes))) { _ = Execute(() => Action(ids => store.Trash(ids))); e.Handled = true; }
    }
    async Task RunBackground(CancellationToken ct)
    {
        var scanAt = DateTime.MinValue; try
        {
            while (!ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow - scanAt > TimeSpan.FromSeconds(15)) { scanAt = DateTime.UtcNow; try { var result = await Task.Run(() => mobile.ScanAsync(ct), ct); if (result.Issues.Count > 0) await Dispatcher.UIThread.InvokeAsync(() => status.Text = "Mobile: " + result.Issues[0].Message); } catch (Exception e) when (e is not OperationCanceledException) { await Dispatcher.UIThread.InvokeAsync(() => status.Text = e.Message); } }
                ct.ThrowIfCancellationRequested(); await Task.Run(() => runner.RunNextAsync(ct), ct); ct.ThrowIfCancellationRequested(); foreach (var done in store.Jobs.Where(j => j.State == JobState.Completed && !j.Trashed)) { try { ct.ThrowIfCancellationRequested(); await Task.Run(() => store.Export(done.Id), ct); } catch (Exception e) when (e is not OperationCanceledException) { store.SetExportError(done.Id, e.Message); } }
                foreach (var j in store.Jobs.Where(j => j.State == JobState.Completed && !j.Trashed && j.Mobile != null)) { try { ct.ThrowIfCancellationRequested(); await Task.Run(() => mobile.Publish(j.Id), ct); } catch (Exception e) when (e is not OperationCanceledException) { await Dispatcher.UIThread.InvokeAsync(() => status.Text = "Mobile result: " + e.Message); } }
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
    IEnumerable<string> Selected() => jobs.SelectedItems?.Cast<JobChoice>().Select(x => x.Job.Id).ToArray() ?? [];
    RecordingJob? Current() => jobs.SelectedItem is JobChoice choice ? store.Get(choice.Job.Id) : null;
    static DateTimeOffset? SortDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    static string DisplayDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy.MM.dd · HH:mm", CultureInfo.InvariantCulture) : "녹음 날짜 미확인";
    static string StateLabel(JobState state) => state switch { JobState.Queued => "처리 대기", JobState.Transcribing => "전사 중", JobState.GeneratingNotes => "노트 생성 중", JobState.Completed => "완료", JobState.Failed => "처리 실패", JobState.Cancelled => "취소됨", _ => "삭제됨" };
    void Refresh()
    {
        if (closing) return;
        var all = store.Jobs; var queued = all.Count(j => !j.Trashed && j.State == JobState.Queued);
        var active = all.FirstOrDefault(j => j.State is JobState.Transcribing or JobState.GeneratingNotes);
        queueInfo.Text = (store.Settings.QueuePaused ? "새 작업 일시중지" : active != null ? StateLabel(active.State) : "처리 중인 작업 없음") + (queued > 0 ? $" · 대기 {queued}개" : "");
        var choices = FolderChoices();
        var signature = System.Text.Json.JsonSerializer.Serialize(all) + System.Text.Json.JsonSerializer.Serialize(choices) + view + selectedFolder + search.Text + store.Settings.QueuePaused;
        if (signature == lastSignature) { UpdateActions(); return; }
        lastSignature = signature;
        var selected = Selected().ToHashSet(); var primary = Current()?.Id; refreshing = true;
        try
        {
            navigation.ItemsSource = choices.Take(3).ToArray(); navigation.SelectedItem = choices.Take(3).FirstOrDefault(f => f.View == view);
            folders.ItemsSource = choices.Skip(3).ToArray(); folders.SelectedItem = choices.Skip(3).FirstOrDefault(f => f.Id == selectedFolder && view == "folder");
            var visible = all.Where(j => (j.State != JobState.Purged || (view == "trash" && IsPendingPurge(j))) && j.Trashed == (view == "trash") && (view is "all" or "trash" || (view == "unfiled" ? j.FolderId == null : j.FolderId == selectedFolder)));
            var query = search.Text?.Trim() ?? ""; var list = visible.Where(j => Matches(j, query)).OrderByDescending(j => SortDate(j.RecordedAt)).ThenBy(j => j.Id, StringComparer.Ordinal).Select(j => new JobChoice(j)).ToArray();
            jobs.ItemsSource = list;
            if (list.FirstOrDefault(x => x.Job.Id == primary) is { } previous) jobs.SelectedItem = previous;
            foreach (var item in list.Where(x => selected.Contains(x.Job.Id))) if (jobs.SelectedItems?.Contains(item) == false) jobs.SelectedItems.Add(item);
            listTitle.Text = view == "folder" ? FolderPath(selectedFolder) : choices.FirstOrDefault(f => f.View == view)?.Label ?? "전체 녹음"; listCount.Text = list.Length.ToString();
            emptyList.IsVisible = list.Length == 0;
            emptyList.Text = query.Length > 0 ? "검색 결과가 없습니다.\n검색어를 바꿔 보세요." : view == "trash" ? "휴지통이 비어 있습니다." : "아직 녹음이 없습니다.\n완료된 오디오 파일을 가져오세요.";
            foreach (var id in searchCache.Keys.Where(id => !all.Any(j => j.Id == id && j.State != JobState.Purged)).ToArray()) searchCache.Remove(id);
        }
        finally { refreshing = false; }
        Detail();
    }
    bool Matches(RecordingJob job, string query)
    {
        if (query.Length == 0 || job.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (job.State == JobState.Purged) return false;
        var signature = $"{job.State}:{job.ArtifactRevision}:{job.PendingArtifactRevision}";
        if (!searchCache.TryGetValue(job.Id, out var cached) || cached.Signature != signature)
        {
            try { cached = (signature, store.ReadTranscript(job.Id) + "\n" + store.ReadNotes(job.Id)); searchCache[job.Id] = cached; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { status.Text = "검색 중 결과 파일을 읽지 못했습니다: " + e.Message; return false; }
        }
        return cached.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    List<FolderChoice> FolderChoices()
    {
        var all = store.Jobs.Where(j => j.State != JobState.Purged || IsPendingPurge(j)).ToArray();
        var result = new List<FolderChoice> { new(null, "전체 녹음", "all", Count: all.Count(j => !j.Trashed)), new(null, "미분류", "unfiled", Count: all.Count(j => !j.Trashed && j.FolderId == null)), new(null, "휴지통", "trash", Count: all.Count(j => j.Trashed)) };
        void Add(string? parent, int depth) { foreach (var f in store.Folders.Where(f => f.ParentId == parent).OrderBy(f => f.Name)) { result.Add(new(f.Id, f.Name, Depth: depth, Count: all.Count(j => !j.Trashed && j.FolderId == f.Id), HasChildren: store.Folders.Any(child => child.ParentId == f.Id), Expanded: !collapsedFolders.Contains(f.Id))); if (!collapsedFolders.Contains(f.Id)) Add(f.Id, depth + 1); } }
        Add(null, 0); return result;
    }
    void ToggleFolder(string id)
    {
        if (closing) return;
        if (!collapsedFolders.Add(id)) collapsedFolders.Remove(id);
        lastSignature = ""; Refresh();
    }
    Task EditTitle() { if (Current() is { State: not JobState.Purged }) { title.Focus(); title.SelectAll(); } return Task.CompletedTask; }
    bool HasText(bool note) => Current() is { State: not JobState.Purged } && !string.IsNullOrWhiteSpace(note ? notes.Text : transcript.Text);
    bool HasResultText() => HasText(tabs.SelectedIndex == 1);
    async Task OpenResults()
    {
        var path = Current()?.ExportPath; if (path == null || !File.Exists(path)) throw new IOException("먼저 ZIP을 내보내세요.");
        SafeFiles.NoLinks(path);
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (!await Launcher.LaunchDirectoryInfoAsync(directory)) throw new IOException("결과 폴더를 열 수 없습니다.");
        status.Text = "ZIP 결과 폴더를 열었습니다.";
    }
    async Task CopyText()
    {
        if (!HasResultText()) return;
        var clipboard = Clipboard ?? throw new InvalidOperationException("클립보드를 사용할 수 없습니다.");
        await clipboard.SetTextAsync(tabs.SelectedIndex == 1 ? notes.Text : transcript.Text);
        status.Text = "현재 탭의 내용을 복사했습니다.";
    }
    async Task ExportText(bool note, bool markdown)
    {
        var job = Current(); if (job == null || !HasText(note)) return;
        var text = note ? store.ReadNotes(job.Id) : store.ReadTranscript(job.Id);
        var extension = markdown ? "md" : "txt";
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = note ? "노트 Markdown 저장" : "전사 저장", ShowOverwritePrompt = true, SuggestedFileName = SafeFiles.FileTitle(job.Title) + (note ? "-notes." : "-transcript.") + extension, DefaultExtension = extension, FileTypeChoices = new[] { new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = new[] { "*." + extension } } } }).WaitAsync(work.Token);
        if (file == null) return;
        work.Token.ThrowIfCancellationRequested();
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
        await using var writer = new StreamWriter(stream); await writer.WriteAsync(text);
        status.Text = "텍스트를 저장했습니다: " + file.Name;
    }
    string FolderPath(string? id)
    {
        if (id == null) return "미분류";
        var names = new List<string>(); var seen = new HashSet<string>();
        while (id != null && seen.Add(id)) { var f = store.Folders.FirstOrDefault(f => f.Id == id); if (f == null) break; names.Insert(0, f.Name); id = f.ParentId; }
        return string.Join(" / ", names);
    }
    void UpdateActions() { foreach (var action in actions) action.Control.IsEnabled = !closing && action.Enabled(); }
    void Detail()
    {
        if (refreshing || closing) return;
        var j = Current(); var count = Selected().Count(); selectionInfo.Text = count > 0 ? $"{count}개 선택 · Ctrl/Shift" : "Ctrl/Shift 다중 선택";
        detailBody.IsVisible = j != null; emptyDetail.IsVisible = j == null; UpdateActions();
        emptyDetailTitle.Text = view == "trash" ? "휴지통" : "기록을 선택하세요";
        emptyDetailHelp.Text = view == "trash" ? "삭제한 녹음을 선택해 복원하거나 Vemo의 소유 사본을 영구 삭제할 수 있습니다. 원본과 내보낸 ZIP은 별개입니다." : "전사와 노트를 한곳에서 읽고 정리하세요.\n완료된 오디오를 가져오면 설정한 로컬 처리기로 작업합니다.";
        emptyImport.IsVisible = view != "trash";
        if (j == null) { detailId = null; editing = false; return; }
        if (detailId != j.Id || !editing)
        {
            refreshing = true; title.Text = j.Title; date.Text = j.RecordedAt ?? ""; refreshing = false; editing = false; detailId = j.Id;
        }
        var pendingPurge = IsPendingPurge(j);
        title.IsReadOnly = pendingPurge; date.IsReadOnly = pendingPurge || j.Mobile != null; tabs.IsVisible = !pendingPurge;
        if (pendingPurge)
        {
            detailPath.Text = "휴지통 · 영구 삭제 미완료"; detailState.Text = "영구 삭제를 다시 시도하세요";
            transcript.Text = ""; notes.Text = ""; renderedNotes.Children.Clear();
            jobInfo.Text = "소유 사본의 일부 삭제가 완료되지 않았습니다. 복원하거나 결과를 읽을 수 없습니다. 작업 메뉴에서 소유 사본 영구 삭제를 다시 실행하세요.\n" + j.Error;
            jobInfo.Foreground = NativeWhite.Danger; stateBanner.IsVisible = true; stateBanner.Background = Brush.Parse("#FBEFF0"); UpdateActions(); return;
        }
        ToolTip.SetTip(date, j.Mobile != null ? "모바일 녹음 날짜는 원본에서 유지되며 수정할 수 없습니다." : "예: 2026-10-18T10:00:00+09:00 · Enter 저장 / Escape 취소");
        detailPath.Text = FolderPath(j.FolderId) + (j.Trashed ? " · 휴지통" : "");
        detailState.Text = StateLabel(j.State) + " · " + j.Models.WhisperProfile + " / " + j.Models.NotesProfile + " · " + j.Models.Compute;
        var messages = new List<string>();
        if (j.State == JobState.Queued) messages.Add(store.Settings.QueuePaused ? "새 작업이 일시중지되어 대기 중입니다. 처리 메뉴나 설정에서 재개할 수 있습니다." : "처리 대기 중입니다. 현재 결과는 계속 읽을 수 있습니다.");
        else if (j.State is JobState.Transcribing or JobState.GeneratingNotes) messages.Add(StateLabel(j.State) + " · 완료된 결과가 있으면 아래에 유지됩니다.");
        else if (j.State == JobState.Cancelled) messages.Add("작업이 취소되었습니다. 선택 작업 메뉴에서 재시도할 수 있습니다.");
        if (j.RecordedAt == null) messages.Add("녹음 날짜 미확인 · 파일 수정일로 대신하지 않습니다.");
        if (j.Error != null) messages.Add("처리: " + j.Error); if (j.ExportError != null) messages.Add("ZIP 저장: " + j.ExportError); if (j.ExportPath != null) messages.Add("ZIP: " + j.ExportPath);
        if (j.State != JobState.Completed && j.ArtifactRevision != null) messages.Add("이전 완료 결과는 유지됩니다.");
        jobInfo.Text = string.Join("\n", messages); jobInfo.Foreground = j.Error != null || j.ExportError != null ? NativeWhite.Danger : NativeWhite.Secondary;
        try
        {
            var source = store.ReadTranscript(j.Id); var markdown = store.ReadNotes(j.Id);
            if (transcript.Text != source) transcript.Text = source; if (notes.Text != markdown) { notes.Text = markdown; RenderNotes(markdown); }
            if (renderedNotes.Children.Count == 0) RenderNotes(markdown);
            transcript.PlaceholderText = "아직 전사가 없습니다. 처리 상태와 로컬 모델 설정을 확인하세요.";
            notes.PlaceholderText = "아직 노트가 없습니다. 전사가 있으면 재시도로 이어서 처리할 수 있습니다.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { transcript.Text = ""; notes.Text = ""; RenderNotes(""); jobInfo.Text += "\n결과 읽기 실패: " + e.Message; jobInfo.Foreground = NativeWhite.Danger; }
        stateBanner.IsVisible = !string.IsNullOrWhiteSpace(jobInfo.Text);
        stateBanner.Background = jobInfo.Foreground == NativeWhite.Danger ? Brush.Parse("#FBEFF0") : NativeWhite.Sidebar;
        UpdateActions();
    }
    static readonly string[] audioExtensions = [".wav", ".mp3", ".m4a", ".mp4", ".flac", ".ogg", ".aac", ".wma"];
    static IEnumerable<IStorageItem> DroppedFiles(DragEventArgs e) => e.DataTransfer.TryGetFiles() ?? [];
    static bool IsAudioFile(IStorageItem file) => file is IStorageFile && file.TryGetLocalPath() is { } path && File.Exists(path) && audioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    Task Import() => ImportFiles(async () => await StorageProvider.OpenFilePickerAsync(new() { Title = "완료된 오디오 파일 가져오기", AllowMultiple = true, FileTypeFilter = new[] { new FilePickerFileType("오디오") { Patterns = audioExtensions.Select(ext => "*" + ext).ToArray() } } }).WaitAsync(work.Token));
    async Task ImportFiles(Func<Task<IEnumerable<IStorageItem>>> choose)
    {
        if (importing) return; importing = true; UpdateActions();
        try
        {
            var destination = selectedFolder; var files = (await choose()).ToArray(); work.Token.ThrowIfCancellationRequested();
            string? lastImported = null; var imported = 0; var skipped = 0; var failures = new List<string>();
            foreach (var file in files)
            {
                work.Token.ThrowIfCancellationRequested();
                if (!IsAudioFile(file)) { skipped++; continue; }
                try { var j = store.Import(file.TryGetLocalPath()!); lastImported = j.Id; imported++; if (destination != null) store.Move([j.Id], destination); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { failures.Add(file.Name + ": " + e.Message); }
            }
            if (lastImported != null)
            {
                selectedFolder = store.Get(lastImported).FolderId; view = selectedFolder == null ? "unfiled" : "folder"; search.Text = ""; lastSignature = ""; Refresh();
                jobs.SelectedItems?.Clear(); jobs.SelectedItem = jobs.ItemsSource!.Cast<JobChoice>().FirstOrDefault(item => item.Job.Id == lastImported);
                if (jobs.SelectedItem != null) jobs.ScrollIntoView(jobs.SelectedItem);
            }
            if (files.Length > 0) status.Text = $"{imported}개 가져옴 · {skipped}개 지원하지 않는 파일 건너뜀 · {failures.Count}개 오류. 원본은 유지됩니다." + (failures.Count > 0 ? "\n" + string.Join("\n", failures.Take(3)) : " 녹음 날짜를 확인하세요.");
        }
        finally { importing = false; UpdateActions(); }
    }
    void RenderNotes(string markdown)
    {
        renderedNotes.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) { renderedNotes.Children.Add(NativeWhite.Text("아직 노트가 없습니다. 처리 상태를 확인하고 실패하거나 취소된 작업은 선택 작업 메뉴에서 재시도하세요.", "secondary")); return; }
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var heading = line.StartsWith("### ") ? 3 : line.StartsWith("## ") ? 2 : line.StartsWith("# ") ? 1 : 0;
            var bullet = line.StartsWith("- ") || line.StartsWith("* ");
            var text = new SelectableTextBlock { Text = heading > 0 ? line[(heading + 1)..] : bullet ? "• " + line[2..] : line, TextWrapping = TextWrapping.Wrap, FontSize = heading == 1 ? 22 : heading == 2 ? 18 : 15, FontWeight = heading > 0 ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(bullet ? 12 : 0, heading > 0 ? 8 : 0, 0, 0) };
            // Source reference text remains literal. No invented links, citations, or timestamps.
            renderedNotes.Children.Add(text);
        }
    }
    Task SaveTitle()
    {
        var j = Current(); if (j != null && j.State != JobState.Purged) { SafeFiles.Title(title.Text ?? ""); SafeFiles.RecordedAt(string.IsNullOrWhiteSpace(date.Text) ? null : date.Text); store.Rename(j.Id, title.Text ?? ""); store.SetRecordedAt(j.Id, string.IsNullOrWhiteSpace(date.Text) ? null : date.Text); editing = false; status.Text = "제목과 녹음 날짜를 저장했습니다."; } return Task.CompletedTask;
    }
    Task Action(Action<string[]> action) { action(Selected().ToArray()); return Task.CompletedTask; }
    async Task<string?> ChooseFolder(string? movingFolder = null)
    {
        var choices = new List<FolderChoice> { new(null, "미분류 / 최상위") };
        bool Descendant(string id) { var seen = new HashSet<string>(); while (seen.Add(id)) { if (id == movingFolder) return true; var f = store.Folders.FirstOrDefault(f => f.Id == id); if (f?.ParentId == null) return false; id = f.ParentId; } return true; }
        choices.AddRange(store.Folders.Where(f => movingFolder == null || !Descendant(f.Id)).Select(f => new FolderChoice(f.Id, FolderPath(f.Id))).OrderBy(f => f.Label));
        var combo = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch }; NativeWhite.Label(combo, "이동할 폴더");
        var dialog = Dialog("폴더 이동", 460, 230); var stack = new StackPanel { Margin = new Thickness(24), Spacing = 16 }; stack.Children.Add(NativeWhite.Text("이동할 폴더", "heading")); stack.Children.Add(combo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(NativeWhite.Action("취소", () => dialog.Close(false))); var ok = NativeWhite.Action("이동", () => dialog.Close(true), "primary"); ok.IsDefault = true; buttons.Children.Add(ok); stack.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = stack }; dialog.Opened += (_, _) => combo.Focus();
        return await dialog.ShowDialog<bool>(this).WaitAsync(work.Token) ? (combo.SelectedItem as FolderChoice)?.Id : throw new OperationCanceledException("Move cancelled.");
    }
    Window Dialog(string caption, double width, double height)
    {
        var dialog = new Window { Title = caption, Width = width, Height = height, MinWidth = width, MinHeight = height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } }; return dialog;
    }
    async Task Move()
    {
        var ids = Selected().ToArray(); var destination = await ChooseFolder(); work.Token.ThrowIfCancellationRequested(); store.Move(ids, destination);
        foreach (var id in ids) if (store.Get(id).State == JobState.Completed && !store.Get(id).Trashed) { try { store.Export(id); } catch (Exception e) { store.SetExportError(id, e.Message); } }
    }
    Task MoveFolder() => MoveFolder(selectedFolder ?? throw new InvalidOperationException("폴더를 먼저 선택하세요."));
    async Task MoveFolder(string id) { var destination = await ChooseFolder(id); work.Token.ThrowIfCancellationRequested(); store.MoveFolder(id, destination); }
    Task CreateFolder() => CreateFolder(selectedFolder);
    async Task CreateFolder(string? parent)
    {
        var input = new TextBox { PlaceholderText = "폴더 이름" }; NativeWhite.Label(input, "새 폴더 이름");
        var parents = new List<FolderChoice> { new(null, "최상위") };
        parents.AddRange(store.Folders.Select(f => new FolderChoice(f.Id, FolderPath(f.Id))).OrderBy(f => f.Label));
        var parentPicker = new ComboBox { ItemsSource = parents, SelectedItem = parents.FirstOrDefault(f => f.Id == parent), HorizontalAlignment = HorizontalAlignment.Stretch }; NativeWhite.Label(parentPicker, "상위 폴더");
        var dialog = Dialog("새 폴더", 420, 350); var stack = new StackPanel { Margin = new Thickness(24), Spacing = 14 }; stack.Children.Add(NativeWhite.Text("새 폴더", "heading")); stack.Children.Add(input); stack.Children.Add(NativeWhite.Text("상위 폴더", "secondary")); stack.Children.Add(parentPicker); var error = NativeWhite.Text("", "error"); stack.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(NativeWhite.Action("취소", () => dialog.Close()));
        var ok = NativeWhite.Action("만들기", () => { try { work.Token.ThrowIfCancellationRequested(); store.CreateFolder(input.Text ?? "", (parentPicker.SelectedItem as FolderChoice)?.Id); dialog.Close(); } catch (Exception e) { error.Text = e.Message; } }, "primary"); ok.IsDefault = true; buttons.Children.Add(ok); stack.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = stack }; dialog.Opened += (_, _) => input.Focus(); await dialog.ShowDialog(this).WaitAsync(work.Token);
    }
    async Task Purge()
    {
        var ids = Selected().ToArray(); if (ids.Length == 0) return;
        var dialog = Dialog("소유 사본 영구 삭제", 480, 290); var stack = new StackPanel { Margin = new Thickness(24), Spacing = 18 }; stack.Children.Add(NativeWhite.Text($"{ids.Length}개를 영구 삭제할까요?", "heading")); stack.Children.Add(NativeWhite.Text("Vemo가 보관한 녹음 사본, 전사와 노트가 삭제됩니다. 가져온 원본과 이미 내보낸 ZIP은 유지됩니다. 이 작업은 취소할 수 없습니다.", "secondary"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; var cancel = NativeWhite.Action("취소", () => dialog.Close(false)); buttons.Children.Add(cancel); buttons.Children.Add(NativeWhite.Action("영구 삭제", () => dialog.Close(true), "danger")); stack.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = stack }; dialog.Opened += (_, _) => cancel.Focus();
        if (await dialog.ShowDialog<bool>(this).WaitAsync(work.Token)) { work.Token.ThrowIfCancellationRequested(); store.Purge(ids); status.Text = "선택한 소유 사본을 삭제했습니다. 원본과 내보낸 ZIP은 유지됩니다."; }
    }
    Task Cancel() { var ids = Selected().ToArray(); store.CancelQueued(ids); if (runner.ActiveJobId != null && ids.Contains(runner.ActiveJobId)) runner.CancelActive(); return Task.CompletedTask; }
    async Task Export() { foreach (var id in Selected()) { work.Token.ThrowIfCancellationRequested(); await Task.Run(() => store.Export(id), work.Token); } status.Text = "ZIP 내보내기를 완료했습니다."; }
    async Task Publish() { foreach (var id in Selected()) { work.Token.ThrowIfCancellationRequested(); await Task.Run(() => mobile.Publish(id), work.Token); } status.Text = "로컬 result.json을 게시했습니다. 클라우드 업로드 완료를 뜻하지 않습니다."; }
    async Task Scan() { var r = await Task.Run(() => mobile.ScanAsync(work.Token)); status.Text = $"모바일: {r.Imported}개 가져옴, {r.Skipped}개 건너뜀. " + string.Join("; ", r.Issues.Select(i => i.Message).Take(3)); }
}
