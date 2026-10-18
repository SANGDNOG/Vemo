using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Vemo.Core;
using Vemo.Runtime;
using System.Globalization;
namespace Vemo.App;

public sealed partial class MainWindow : Window
{
    static readonly HttpClient runtimeHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly RuntimePreparation runtimePreparation; ModelSelection? preparedSettings;
    readonly LibraryStore store; readonly JobRunner runner; readonly MobileFolderAdapter mobile; readonly WatchedFolderImporter watched; readonly GoogleConnectionService google;
    readonly ListBox navigation = new(), folders = new(), jobs = new() { SelectionMode = SelectionMode.Multiple };
    readonly TextBox search = new() { PlaceholderText = "제목 · 전사 · 노트 검색" }, title = new() { IsReadOnly = true }, date = new() { PlaceholderText = "녹음 시작일 YYYY-MM-DD" };
    readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap }, notes = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock status = NativeWhite.Text("", "secondary"), jobInfo = NativeWhite.Text("", "secondary"), queueInfo = NativeWhite.Text("", "secondary"), listTitle = NativeWhite.Text("전체 녹음"), listCount = NativeWhite.Text("", "secondary"), selectionInfo = NativeWhite.Text("", "caption"), detailPath = NativeWhite.Text("", "secondary"), detailState = NativeWhite.Text("", "secondary");
    readonly Button emptyImport;
    TrayController? tray; bool exitRequested;
    readonly AudioPlayerView player = new();
    readonly TranscriptView transcriptCards = new();
    readonly ProcessingProgressView processingProgress = new(), notesProgress = new();
    readonly ProgressBar queueProgress = new() { Width = 55, Height = 3, Foreground = NativeWhite.Accent, Background = NativeWhite.Divider, IsVisible = false };
    readonly TextBlock recordedDate = NativeWhite.Text("", "caption");
    Grid? dateEditor;
    bool titleEditing;
    readonly Grid detailHost = new();
    Grid? recordingDetail; SettingsView? settingsView; Control? settingsInvoker;
    readonly TabControl tabs = new(); readonly Grid detailBody = new(); readonly StackPanel emptyDetail = new(); readonly TextBlock emptyList = NativeWhite.Text("", "secondary");
    readonly WindowWork work = new(); readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<(Control Control, Func<bool> Enabled)> actions = [];
    readonly Dictionary<string, (string Signature, string Text)> searchCache = [];
    readonly HashSet<string> collapsedFolders = [];
    string? selectedFolder, detailId; string view = "all"; bool refreshing, refreshPending, closing, closeReady, editing, importing; Task backgroundTask = Task.CompletedTask; string lastSignature = ""; int focusPane;
    record FolderChoice(string? Id, string Label, string View = "folder", int Depth = 0, int Count = 0) { public override string ToString() => Label; }
    record JobChoice(RecordingJob Job) { public override string ToString() => $"{Job.Title} · {StateLabel(Job.State)}"; }
    public MainWindow() : this(null) { }
    public MainWindow(string? libraryRoot)
    {
        Title = "Vemo"; Width = 1280; Height = 820; MinWidth = 980; MinHeight = 540;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Vemo.App/Assets/vemo-icon.png")));
        store = new(libraryRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vemo", "WindowsPreview")); runtimePreparation = new(Path.Combine(store.Root, "runtime-auto"), runtimeHttp); runner = new(store, new LocalCliInferenceEngine(), runtimePreparation); mobile = new(store); watched = new(store); google = GoogleConnectionService.Shared(store);
        navigation.Classes.Add("navigation"); folders.Classes.Add("navigation"); jobs.Classes.Add("recordings"); search.Classes.Add("search"); title.Classes.Add("title"); transcript.Classes.Add("reading"); notes.Classes.Add("reading");
        NativeWhite.Label(navigation, "라이브러리", "방향키로 전체 녹음, 미분류, 휴지통 선택"); NativeWhite.Label(folders, "폴더", "방향키로 선택, F6으로 다음 영역"); NativeWhite.Label(jobs, "녹음 목록", "Ctrl/Shift 다중 선택, Ctrl+A 전체 선택, Delete 휴지통");
        NativeWhite.Label(search, "현재 목록의 제목, 전사, 노트 검색", "Ctrl+F 검색, Escape 지우기"); NativeWhite.Label(title, "녹음 제목", "Enter 저장, Escape 변경 취소"); NativeWhite.Label(date, "녹음 날짜", "실제 녹음 시작일 YYYY-MM-DD, 모르면 비워 두세요"); NativeWhite.Label(transcript, "전사 원문"); NativeWhite.Label(notes, "생성된 노트, Markdown 원문");
        folders.ItemTemplate = new FuncDataTemplate<FolderChoice>((f, _) =>
        {
            // Avalonia clears Content to null when recycling an item container.
            if (f == null) return null;
            var row = new Grid { ColumnDefinitions = new(f.Id != null ? "12,10,19,10,*,Auto,8,32" : "19,10,*,Auto"), Margin = new Thickness(f.Depth * 19, 0, 0, 0) };
            if (f.Id != null && store.Folders.Any(x => x.ParentId == f.Id))
            {
                var disclosure = NativeWhite.Action("", () => { if (!collapsedFolders.Add(f.Id)) collapsedFolders.Remove(f.Id); RequestRefresh(); }, "quiet");
                disclosure.Content = VemoIcons.Outline(collapsedFolders.Contains(f.Id) ? "chevron" : "down", 12);
                disclosure.Padding = new Thickness(0); disclosure.MinHeight = 28; disclosure.Width = 12;
                NativeWhite.Label(disclosure, f.Label + " 하위 폴더 " + (collapsedFolders.Contains(f.Id) ? "펼치기" : "접기")); row.Children.Add(disclosure);
            }
            var icon = VemoIcons.Outline(f.View); Grid.SetColumn(icon, f.Id != null ? 2 : 0); row.Children.Add(icon);
            var label = new TextBlock { Text = f.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }; ToolTip.SetTip(label, f.Label); Grid.SetColumn(label, f.Id != null ? 4 : 2); row.Children.Add(label);
            var count = NativeWhite.Text(f.Count.ToString(), "caption"); count.FontSize = 11; count.VerticalAlignment = VerticalAlignment.Center; count.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(count, f.Id != null ? 5 : 3); row.Children.Add(count);
            if (f.Id != null)
            {
                var manage = new Button { Content = VemoIcons.Outline("more"), Padding = new Thickness(0), MinHeight = 32, Height = 32, CornerRadius = new CornerRadius(8), Width = 32, HorizontalAlignment = HorizontalAlignment.Right }; manage.Classes.Add("quiet"); NativeWhite.Label(manage, f.Label + " 폴더 관리");
                manage.Click += (_, _) => { selectedFolder = f.Id; view = "folder"; folders.SelectedItem = f; UpdateActions(); folders.ContextMenu?.Open(folders); };
                manage.AddHandler(PointerPressedEvent, (_, _) => { selectedFolder = f.Id; view = "folder"; folders.SelectedItem = f; UpdateActions(); }, RoutingStrategies.Tunnel);
                manage.GotFocus += (_, _) => { selectedFolder = f.Id; view = "folder"; folders.SelectedItem = f; UpdateActions(); };
                Grid.SetColumn(manage, 7); row.Children.Add(manage);
            }
            return row;
        });
        navigation.ItemTemplate = folders.ItemTemplate;
        folders.ContextMenu = new ContextMenu { ItemsSource = FolderMenu() };
        jobs.ItemTemplate = new FuncDataTemplate<JobChoice>((choice, _) =>
        {
            if (choice == null) return null;
            var j = choice.Job; var row = new StackPanel { Spacing = 7 };
            row.Children.Add(new TextBlock { Text = RecordingTitles.Display(j), FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.NoWrap, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            var recorded = NativeWhite.Text(RecordingTitles.Day(j) ?? "녹음 날짜 미확인", "caption"); recorded.FontSize = 10; ToolTip.SetTip(recorded, j.RecordedAt ?? "녹음 날짜 미확인"); row.Children.Add(recorded);
            var rowState = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; rowState.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brush.Parse("#B9ACD9"), VerticalAlignment = VerticalAlignment.Center });
            var stateText = NativeWhite.Text(StateLabel(j.State) + (j.ExportError != null ? " · ZIP 저장 실패" : ""), j.State == JobState.Failed || j.ExportError != null ? "error" : "caption"); stateText.FontSize = 10; rowState.Children.Add(stateText); row.Children.Add(rowState);
            ToolTip.SetTip(row, j.Title + "\n" + (j.Error ?? j.ExportError ?? StateLabel(j.State)));
            var selectedRow = new Grid { ColumnDefinitions = new("*,20") }; selectedRow.Children.Add(row); var mark = NativeWhite.SelectionMark(); Grid.SetColumn(mark, 1); selectedRow.Children.Add(mark); return selectedRow;
        });
        var outer = new Grid { RowDefinitions = new("*,Auto") };
        var columns = new Grid { ColumnDefinitions = new("220,300,*") }; outer.Children.Add(columns);
        var sidebar = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), Background = NativeWhite.Sidebar };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(22, 21, 20, 28) };
        brand.Children.Add(new Image { Width = 27, Height = 27, Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Vemo.App/Assets/vemo-icon.png"))) }); brand.Children.Add(new TextBlock { Text = "Vemo", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }); sidebar.Children.Add(brand);
        var libraryHeading = NativeWhite.Text("라이브러리", "caption"); libraryHeading.FontSize = 10; libraryHeading.Margin = new Thickness(22, 0, 12, 11); Grid.SetRow(libraryHeading, 1); sidebar.Children.Add(libraryHeading);
        Grid.SetRow(navigation, 2); sidebar.Children.Add(navigation);
        var folderHeading = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(22, 16, 16, 2) }; var folderLabel = NativeWhite.Text("폴더", "caption"); folderLabel.FontSize = 11; folderLabel.VerticalAlignment = VerticalAlignment.Center; folderHeading.Children.Add(folderLabel);
        var create = Button("+ 폴더", CreateFolder, "soft"); create.FontSize = 11; create.MinHeight = 30; create.Padding = new Thickness(10, 8); create.Margin = new Thickness(0); Grid.SetColumn(create, 1); folderHeading.Children.Add(create); Grid.SetRow(folderHeading, 3); sidebar.Children.Add(folderHeading);
        Grid.SetRow(folders, 4); sidebar.Children.Add(folders);
        var sidebarBottom = new StackPanel { Margin = new Thickness(10, 12, 10, 12) };
        var settings = Button("설정", OpenSettings, "quiet"); settings.HorizontalAlignment = HorizontalAlignment.Stretch; settings.HorizontalContentAlignment = HorizontalAlignment.Left; settings.Height = 40; settings.FontSize = 11; settings.Padding = new Thickness(12, 0); settings.Margin = new Thickness(0);
        NativeWhite.Label(settings, "설정");
        settings.ContentTemplate = new FuncDataTemplate<string>((text, _) => { var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; content.Children.Add(VemoIcons.Outline("settings")); content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }); return content; });
        sidebarBottom.Children.Add(settings); Grid.SetRow(sidebarBottom, 5); sidebar.Children.Add(sidebarBottom);
        columns.Children.Add(NativeWhite.Panel(sidebar, true));
        var listPane = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        var listHeader = new StackPanel { Margin = new Thickness(19, 22, 19, 17), Spacing = 15 };
        var heading = new Grid { ColumnDefinitions = new("*,Auto") }; listTitle.FontSize = 16; listTitle.FontWeight = FontWeight.SemiBold; heading.Children.Add(listTitle); listCount.FontSize = 11; listCount.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(listCount, 1); heading.Children.Add(listCount); listHeader.Children.Add(heading);
        var searchBox = new Grid(); searchBox.Children.Add(search); search.FontSize = 12; search.Padding = new Thickness(40, 10, 12, 10); var searchIcon = VemoIcons.Outline("search"); searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.Margin = new Thickness(12, 0, 0, 0); searchBox.Children.Add(searchIcon); listHeader.Children.Add(searchBox); listPane.Children.Add(listHeader);
        var selection = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(19, 0, 19, 9) }; selectionInfo.FontSize = 10; selectionInfo.VerticalAlignment = VerticalAlignment.Center; selection.Children.Add(selectionInfo);
        var menuButton = new Button { Content = "선택", FontSize = 10, Padding = new Thickness(6), MinHeight = 26, Flyout = new MenuFlyout { ItemsSource = SelectionMenu() } }; menuButton.Classes.Add("quiet"); NativeWhite.Label(menuButton, "선택 작업 ⋯"); Grid.SetColumn(menuButton, 1); selection.Children.Add(menuButton); Grid.SetRow(selection, 1); listPane.Children.Add(selection);
        var listArea = new Grid(); listArea.Children.Add(jobs); emptyList.Margin = new Thickness(24); emptyList.VerticalAlignment = VerticalAlignment.Center; emptyList.TextAlignment = TextAlignment.Center; listArea.Children.Add(emptyList); Grid.SetRow(listArea, 2); listPane.Children.Add(listArea);
        jobs.ContextMenu = new ContextMenu { ItemsSource = SelectionMenu() };
        var importRow = new Grid { ColumnDefinitions = new("*,Auto") }; var importLabel = NativeWhite.Text("오디오 파일 가져오기", "secondary"); importLabel.FontSize = 10; importLabel.VerticalAlignment = VerticalAlignment.Center; importRow.Children.Add(importLabel); var import = Button("+", Import, "quiet", () => !importing); NativeWhite.Label(import, "오디오 파일 가져오기", "Ctrl+O"); import.Classes.Add("icon-action"); import.Margin = new Thickness(0); import.ContentTemplate = new FuncDataTemplate<string>((_, _) => VemoIcons.Outline("plus", 24)); ToolTip.SetTip(import, "Ctrl+O · 녹음이 끝난 파일을 선택하세요"); Grid.SetColumn(import, 1); importRow.Children.Add(import); var importBorder = new Border { Child = importRow, Padding = new Thickness(18, 14), BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(importBorder, 3); listPane.Children.Add(importBorder);
        var listBorder = NativeWhite.Panel(listPane, true); Grid.SetColumn(listBorder, 1); columns.Children.Add(listBorder);
        var detail = new Grid { RowDefinitions = new("58,*") }; recordingDetail = detail; detailHost.Children.Add(detail); Grid.SetColumn(detailHost, 2); columns.Children.Add(detailHost);
        var top = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(27, 0) }; queueInfo.VerticalAlignment = VerticalAlignment.Center; queueInfo.FontSize = 10;
        var queueMenu = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { queueProgress, queueInfo } }, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Flyout = new MenuFlyout { ItemsSource = new Control[] { Menu("새 작업 일시중지 / 재개", ToggleQueue), Menu("모바일 폴더 지금 확인", Scan), Menu("설정", OpenSettings) } } }; queueMenu.Classes.Add("quiet"); NativeWhite.Label(queueMenu, "처리 상태 메뉴"); top.Children.Add(queueMenu);
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var topImport = Button("파일 가져오기", Import, "quiet", () => !importing); VemoIcons.Decorate(topImport, "import", iconOnly: true);
        var topSettings = Button("환경 설정", OpenSettings, "quiet"); VemoIcons.Decorate(topSettings, "settings", iconOnly: true);
        topActions.Children.Add(topImport); topActions.Children.Add(topSettings);
        Grid.SetColumn(topActions, 1); top.Children.Add(topActions); detail.Children.Add(top);
        var detailArea = new Grid(); Grid.SetRow(detailArea, 1); detail.Children.Add(detailArea);
        emptyDetail.Spacing = 12; emptyDetail.VerticalAlignment = VerticalAlignment.Center; emptyDetail.HorizontalAlignment = HorizontalAlignment.Center; emptyDetail.MaxWidth = 350;
        emptyDetail.Children.Add(NativeWhite.Text("기록을 선택하세요", "heading")); emptyDetail.Children.Add(NativeWhite.Text("전사와 노트를 한곳에서 읽고 정리하세요.\n완료된 오디오를 가져오면 설정한 로컬 처리기로 작업합니다.", "secondary")); emptyImport = Button("오디오 가져오기", Import, "primary", () => !importing); emptyDetail.Children.Add(emptyImport); detailArea.Children.Add(emptyDetail);
        detailBody.RowDefinitions = new("Auto,Auto,Auto,*,Auto");
        detailArea.Children.Add(detailBody);
        title.DoubleTapped += (_, e) => { BeginTitleEdit(); e.Handled = true; };
        var titleRow = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(32, 27, 32, 0) }; titleRow.Children.Add(title);
        var more = new Button { Content = "⋯", Flyout = new MenuFlyout { ItemsSource = SelectionMenu() } }; NativeWhite.Label(more, "녹음 작업 메뉴"); more.Classes.Add("quiet"); VemoIcons.Decorate(more, "more", iconOnly: true); NativeWhite.Label(more, "녹음 작업 메뉴"); Grid.SetColumn(more, 1); titleRow.Children.Add(more); detailBody.Children.Add(titleRow);
        var metadata = new StackPanel { Spacing = 0, Margin = new Thickness(32, 10, 32, 22) }; recordedDate.FontSize = 11; recordedDate.LineHeight = 13; recordedDate.Margin = new Thickness(0, 0, 0, 15); detailPath.FontSize = 11; detailPath.VerticalAlignment = VerticalAlignment.Center; detailPath.Foreground = NativeWhite.Accent; var breadcrumb = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = 26, VerticalAlignment = VerticalAlignment.Center }; breadcrumb.Children.Add(VemoIcons.Outline("folder", 14)); breadcrumb.Children.Add(detailPath); var edit = new Grid { ColumnDefinitions = new("*,Auto") }; dateEditor = edit; metadata.Children.Add(recordedDate); NativeWhite.Label(recordedDate, "녹음 날짜 표시"); edit.Children.Add(date); var save = Button("저장", SaveTitle, "soft"); Grid.SetColumn(save, 1); edit.Children.Add(save); metadata.Children.Add(edit); metadata.Children.Add(breadcrumb); Grid.SetRow(metadata, 1); detailBody.Children.Add(metadata);
        jobInfo.Margin = new Thickness(32, 8); jobInfo.FontSize = 11;
        tabs.SelectionChanged += (_, _) => UpdateActions();
        TabItem ResultTab(string name, string icon, Control body)
        {
            var item = new TabItem { Header = name, Content = body };
            item.HeaderTemplate = new FuncDataTemplate<string>((text, _) => { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Height = 16 }; row.Children.Add(VemoIcons.Outline(icon, 16)); row.Children.Add(new TextBlock { Text = text, FontSize = 12, LineHeight = 16, VerticalAlignment = VerticalAlignment.Center }); return row; });
            return item;
        }
        var transcriptBody = new Grid(); transcriptBody.Children.Add(transcriptCards); transcriptBody.Children.Add(processingProgress);
        tabs.Classes.Add("result-tabs");
        var notesBody = new Grid(); notesBody.Children.Add(renderedNotes); notesBody.Children.Add(notesProgress);
        var notesScroll = new ScrollViewer { Content = notesBody }; notesScroll.SizeChanged += (_, e) => notesBody.MinHeight = Math.Max(0, e.NewSize.Height);
        tabs.ItemsSource = new[] { ResultTab("전사", "text", transcriptBody), ResultTab("노트", "note", notesScroll) };
        player.Margin = new Thickness(32, 0, 32, 22); Grid.SetRow(player, 2); detailBody.Children.Add(player); Grid.SetRow(tabs, 3); detailBody.Children.Add(tabs);
        var footerArea = new StackPanel(); var errorScroll = new ScrollViewer { Content = jobInfo, MaxHeight = 80, IsVisible = false }; jobInfo.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) errorScroll.IsVisible = jobInfo.IsVisible; }; footerArea.Children.Add(errorScroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(32, 18, 32, 25) };
        var copyResult = Button("복사", CopyResult, null, HasResult); VemoIcons.Decorate(copyResult, "copy"); footer.Children.Add(copyResult);
        var exportResult = Button("내보내기", SaveResult, null, HasResult); VemoIcons.Decorate(exportResult, "export"); footer.Children.Add(exportResult);
        var resultFolder = Button("결과 폴더", OpenResultDirectory, null, () => Current()?.ExportPath != null); VemoIcons.Decorate(resultFolder, "folder"); footer.Children.Add(resultFolder);
        foreach (Button button in footer.Children) { button.Margin = new Thickness(0); button.FontSize = 11; button.Foreground = NativeWhite.Secondary; }
        footerArea.Children.Add(footer); Grid.SetRow(footerArea, 4); detailBody.Children.Add(footerArea);
        void FitDetailHeight()
        {
            var compact = ClientSize.Height < 620;
            titleRow.Margin = new Thickness(32, compact ? 16 : 27, 32, 0);
            metadata.Margin = new Thickness(32, 10, 32, compact ? 12 : 22);
            player.Margin = new Thickness(32, 0, 32, compact ? 12 : 22);
            footer.Margin = new Thickness(32, compact ? 10 : 18, 32, compact ? 12 : 25);
            errorScroll.MaxHeight = compact ? 40 : 80;
        }
        SizeChanged += (_, _) => FitDetailHeight();
        FitDetailHeight();
        processingProgress.PauseRequested += () => _ = Execute(ToggleQueue); notesProgress.PauseRequested += () => _ = Execute(ToggleQueue);
        status.Margin = new Thickness(16, 8); var statusBar = new Border { Child = new ScrollViewer { Content = status, MaxHeight = 70 }, Background = NativeWhite.Sidebar, BorderBrush = NativeWhite.Divider, BorderThickness = new Thickness(0, 1, 0, 0) }; statusBar.IsVisible = !string.IsNullOrWhiteSpace(status.Text); status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) statusBar.IsVisible = !string.IsNullOrWhiteSpace(status.Text); }; Grid.SetRow(statusBar, 1); outer.Children.Add(statusBar); Content = outer;
        transcriptCards.SeekRequested += seconds => player.Seek(seconds);
        runner.ProgressChanged += ProgressChanged;
        WireDragDrop(listArea);
        jobs.AddHandler(PointerPressedEvent, RecordingActivationPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        jobs.AddHandler(PointerReleasedEvent, RecordingActivationReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        navigation.SelectionChanged += (_, _) => { if (refreshing || closing) return; if (navigation.SelectedItem is FolderChoice f) { CloseSettings(); selectedFolder = null; view = f.View; RequestRefresh(); } };
        folders.SelectionChanged += (_, _) => { if (refreshing || closing) return; if (folders.SelectedItem is FolderChoice f) { CloseSettings(); selectedFolder = f.Id; view = f.View; RequestRefresh(); } };
        jobs.SelectionChanged += (_, _) => { if (!refreshing) { CloseSettings(); Detail(); } }; search.TextChanged += (_, _) => { if (!closing) RequestRefresh(); };
        // TextChanged is asynchronous and runs after Detail clears refreshing.
        // Track edits synchronously so loading stored metadata never marks it dirty.
        title.TextChanging += (_, _) => { if (!refreshing) editing = true; }; date.TextChanging += (_, _) => { if (!refreshing) editing = true; };
        title.KeyDown += EditKey; date.KeyDown += EditKey; AddHandler(KeyDownEvent, Shortcut, RoutingStrategies.Tunnel);
        refresh.Tick += (_, _) => Refresh(); Opened += (_, _) => { if (closing) return; tray ??= new TrayController(this, () => store.Settings.QueuePaused, () => _ = Execute(ToggleQueue), () => { exitRequested = true; Close(); }); tray.Enabled = store.Settings.CloseToTray; Refresh(); refresh.Start(); backgroundTask = work.Start(RunBackground); };
        Closing += (_, e) =>
        {
            if (closeReady) return;
            if (!exitRequested && !closing && store.Settings.CloseToTray && tray?.Enabled == true) { e.Cancel = true; player.Stop(); Hide(); return; }
            e.Cancel = true;
            if (closing) return;
            closing = true; refresh.Stop(); player.Stop();
            if (Content is Control content) content.IsEnabled = false;
            status.Text = "진행 중인 작업을 취소하고 저장 작업이 끝날 때까지 기다립니다…";
            _ = CloseAfterDrain();
        };
        Closed += (_, _) => { runner.ProgressChanged -= ProgressChanged; google.Dispose(); player.Dispose(); store.Dispose(); work.Dispose(); };
    }
    async Task CloseAfterDrain()
    {
        var settingsDrain = settingsView?.StopAsync() ?? Task.CompletedTask;
        var drained = work.StopAsync(); runner.CancelActive(); foreach (var owned in OwnedWindows.ToArray()) owned.Close();
        try { await settingsDrain; await drained; } catch (Exception e) { status.Text = "Background stopped: " + e.Message; }
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
    Control[] SelectionMenu() => [Menu("제목 수정", () => { BeginTitleEdit(); return Task.CompletedTask; }, () => Selected().Count() == 1 && Current() is { Trashed: false }), Menu("저장된 노트로 제목 자동 생성", RegenerateTitle, () => Selected().Count() == 1 && !editing && Current() is { Trashed: false, State: not (JobState.Transcribing or JobState.GeneratingNotes or JobState.Purged) }), new Separator(), Menu("선택 이동…", Move, HasSelection), Menu("휴지통으로 이동", () => Action(ids => store.Trash(ids)), () => HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State is not (JobState.Transcribing or JobState.GeneratingNotes)), "Delete", true), Menu("복원", () => Action(ids => store.Restore(ids)), () => HasSelection() && SelectedJobs().All(j => j.Trashed)), Menu("소유 사본 영구 삭제…", Purge, () => HasSelection() && SelectedJobs().All(j => j.Trashed && j.State is not (JobState.Transcribing or JobState.GeneratingNotes)), danger: true), new Separator(), Menu("작업 취소", Cancel, () => HasSelection() && SelectedJobs().Any(j => j.State is JobState.Queued or JobState.Transcribing or JobState.GeneratingNotes)), Menu("기존 모델 설정으로 재시도", () => Action(ids => store.Retry(ids)), Retryable), Menu("현재 모델 설정으로 재시도", () => Action(ids => store.Retry(ids, true)), Retryable), new Separator(), Menu("ZIP 내보내기", Export, () => HasSelection() && SelectedJobs().All(j => j.State == JobState.Completed)), Menu("모바일 결과 게시", Publish, () => HasSelection() && SelectedJobs().All(j => j.State == JobState.Completed && j.Mobile != null && j.Mobile.SyncRoot == store.Settings.MobileDirectory))];
    bool HasSelection() => Selected().Any();
    bool Retryable() => HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State is JobState.Failed or JobState.Cancelled);
    IEnumerable<RecordingJob> SelectedJobs() => Selected().Select(store.Get);
    Task OpenSettings()
    {
        if (settingsView != null) return Task.CompletedTask;
        settingsInvoker = FocusManager?.GetFocusedElement() as Control;
        var pane = new SettingsView(store, mobile, CloseSettings, google, tray?.IsSupported == true, () => runtimePreparation.Status);
        settingsView = pane; player.Stop();
        if (recordingDetail != null) recordingDetail.IsVisible = false;
        detailHost.Children.Add(pane);
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(settingsView, pane)) pane.FocusFirstControl(); });
        return Task.CompletedTask;
    }
    void CloseSettings()
    {
        var pane = settingsView; if (pane == null) return;
        settingsView = null;
        var drain = pane.StopAsync();
        if (!closing) _ = work.Start(async _ => await drain);
        detailHost.Children.Remove(pane);
        if (recordingDetail != null) recordingDetail.IsVisible = true;
        if (!closing && !navigation.IsKeyboardFocusWithin && !folders.IsKeyboardFocusWithin) { if (settingsInvoker?.IsEffectivelyVisible == true) settingsInvoker.Focus(); else jobs.Focus(); }
        lastSignature = "";
    }
    Task ToggleQueue() { store.UpdateSettings(store.Settings with { QueuePaused = !store.Settings.QueuePaused }); return Task.CompletedTask; }
    void BeginTitleEdit()
    {
        if (Current() is not { } job || closing) return;
        titleEditing = true; editing = true; refreshing = true; title.Text = RecordingTitles.BaseTitle(job.Title); refreshing = false;
        title.IsReadOnly = false; title.Focus(); title.SelectAll();
    }
    void EditKey(object? sender, KeyEventArgs e)
    {
        if (sender == title && title.IsReadOnly && e.Key == Key.Enter) { BeginTitleEdit(); e.Handled = true; return; }
        if (e.Key == Key.Enter) { e.Handled = true; _ = Execute(SaveTitle); }
        else if (e.Key == Key.Escape) { titleEditing = false; title.IsReadOnly = true; editing = false; detailId = null; Detail(); e.Handled = true; }
    }
    void Shortcut(object? sender, KeyEventArgs e)
    {
        if (closing) return;
        if (settingsView?.IsKeyboardFocusWithin == true) return;
        if (jobs.IsKeyboardFocusWithin && e.Key is Key.Enter or Key.Space && settingsView != null) { CloseSettings(); Detail(); jobs.Focus(); e.Handled = true; return; }
        if (jobs.IsKeyboardFocusWithin && e.Key == Key.Enter && Current() != null) { BeginTitleEdit(); e.Handled = true; return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F) { search.Focus(); search.SelectAll(); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.O) { _ = Execute(Import); e.Handled = true; }
        else if (e.Key == Key.F6) { focusPane = navigation.IsKeyboardFocusWithin || folders.IsKeyboardFocusWithin ? 0 : jobs.IsKeyboardFocusWithin || search.IsKeyboardFocusWithin ? 1 : 2; focusPane = (focusPane + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 2 : 1)) % 3; (focusPane == 0 ? (Control)(view == "folder" ? folders : navigation) : focusPane == 1 ? search : detailBody.IsVisible ? title : emptyImport).Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape && search.IsKeyboardFocusWithin) { search.Text = ""; jobs.Focus(); e.Handled = true; }
        else if (jobs.IsKeyboardFocusWithin && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A) { jobs.SelectAll(); e.Handled = true; }
        else if (jobs.IsKeyboardFocusWithin && e.Key == Key.Delete && HasSelection() && SelectedJobs().All(j => !j.Trashed && j.State is not (JobState.Transcribing or JobState.GeneratingNotes))) { _ = Execute(() => Action(ids => store.Trash(ids))); e.Handled = true; }
    }
    async Task RunBackground(CancellationToken ct)
    {
        var scanAt = DateTime.MinValue; var inputScanAt = DateTime.MinValue; var googleScanAt = DateTime.MinValue; try
        {
            while (!ct.IsCancellationRequested)
            {
                if (preparedSettings != store.Settings.Models && runner.ActiveJobId == null)
                {
                    var expected = store.Settings.Models;
                    try
                    {
                        var prepared = await Task.Run(() => runtimePreparation.PrepareAsync(expected, ct), ct);
                        if (store.TryApplyPreparedSettingsModels(expected, prepared.Models)) preparedSettings = prepared.Models;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        preparedSettings = expected;
                        await Dispatcher.UIThread.InvokeAsync(() => status.Text = runtimePreparation.Status + " · " + e.Message);
                    }
                }
                if (DateTime.UtcNow - scanAt > TimeSpan.FromSeconds(15)) { scanAt = DateTime.UtcNow; try { var result = await Task.Run(() => mobile.ScanAsync(ct), ct); if (result.Issues.Count > 0) await Dispatcher.UIThread.InvokeAsync(() => status.Text = "Mobile: " + result.Issues[0].Message); } catch (Exception e) when (e is not OperationCanceledException) { await Dispatcher.UIThread.InvokeAsync(() => status.Text = e.Message); } }
                if (DateTime.UtcNow - googleScanAt > TimeSpan.FromSeconds(30) && google.Status.Connected && google.Status.FolderId != null && !google.Status.Busy)
                {
                    googleScanAt = DateTime.UtcNow;
                    try { await Task.Run(() => google.SyncAsync(ct), ct); }
                    catch (Exception e) when (e is not OperationCanceledException) { await Dispatcher.UIThread.InvokeAsync(() => status.Text = "Google Drive: " + e.Message); }
                }
                if (DateTime.UtcNow - inputScanAt > TimeSpan.FromSeconds(5))
                {
                    inputScanAt = DateTime.UtcNow;
                    var input = await Task.Run(() => watched.ScanAsync(store.Settings.InputDirectory, ct), ct);
                    if (input.Issues.Count > 0) await Dispatcher.UIThread.InvokeAsync(() => status.Text = "입력 폴더: " + input.Issues[0].Message);
                    else if (input.Imported > 0) await Dispatcher.UIThread.InvokeAsync(() => status.Text = $"입력 폴더에서 {input.Imported}개 녹음을 가져왔습니다.");
                }
                ct.ThrowIfCancellationRequested();
                try { if (!store.Settings.RequireExternalPower || WindowsPower.HasExternalPower()) await Task.Run(() => runner.RunNextAsync(ct), ct); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* Cancelling runtime preparation must not stop background sync. */ }
                catch (Exception e) when (e is not OperationCanceledException) { await Dispatcher.UIThread.InvokeAsync(() => status.Text = runtimePreparation.Status + " · " + e.Message); }
                ct.ThrowIfCancellationRequested(); foreach (var done in store.Jobs.Where(j => store.Settings.AutoExport && j.State == JobState.Completed && !j.Trashed)) { try { ct.ThrowIfCancellationRequested(); await Task.Run(() => store.Export(done.Id), ct); } catch (Exception e) when (e is not OperationCanceledException) { store.SetExportError(done.Id, e.Message); } }
                foreach (var j in store.Jobs.Where(j => j.State == JobState.Completed && !j.Trashed && j.Mobile != null && j.Mobile.SyncRoot == store.Settings.MobileDirectory)) { try { ct.ThrowIfCancellationRequested(); await Task.Run(() => mobile.Publish(j.Id), ct); } catch (Exception e) when (e is not OperationCanceledException) { await Dispatcher.UIThread.InvokeAsync(() => status.Text = "Mobile result: " + e.Message); } }
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
    IEnumerable<string> Selected() => jobs.SelectedItems?.Cast<JobChoice>().Select(x => x.Job.Id).ToArray() ?? [];
    RecordingJob? Current() => jobs.SelectedItem is JobChoice choice ? store.Get(choice.Job.Id) : null;
    static string DisplayDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy.MM.dd · HH:mm", CultureInfo.InvariantCulture) : "녹음 날짜 미확인";
    static string StateLabel(JobState state) => state switch { JobState.Queued => "처리 대기", JobState.Transcribing => "전사 중", JobState.GeneratingNotes => "노트 생성 중", JobState.Completed => "완료", JobState.Failed => "처리 실패", JobState.Cancelled => "취소됨", _ => "삭제됨" };
    void RequestRefresh()
    {
        lastSignature = "";
        if (refreshPending || closing) return;
        refreshPending = true;
        // Finish the current selection/text event before replacing item sources.
        Dispatcher.UIThread.Post(() => { refreshPending = false; Refresh(); });
    }
    void Refresh()
    {
        if (refreshing || closing || dragging) return;
        if (tray != null) tray.Enabled = store.Settings.CloseToTray;
        var all = store.Jobs; var queued = all.Count(j => !j.Trashed && j.State == JobState.Queued);
        var active = all.FirstOrDefault(j => j.State is JobState.Transcribing or JobState.GeneratingNotes);
        queueInfo.Text = (store.Settings.QueuePaused ? "새 작업 일시중지" : active != null ? StateLabel(active.State) : store.Settings.RequireExternalPower && !WindowsPower.HasExternalPower() ? "전원 연결 대기" : "처리 중인 작업 없음") + (queued > 0 ? $" · 대기 {queued}개" : "");
        var choices = FolderChoices();
        var signature = System.Text.Json.JsonSerializer.Serialize(all) + System.Text.Json.JsonSerializer.Serialize(choices) + view + selectedFolder + search.Text;
        UpdateProgress();
        if (signature == lastSignature) { UpdateActions(); return; }
        lastSignature = signature;
        var selected = Selected().ToHashSet(); var primary = Current()?.Id; refreshing = true;
        try
        {
            var libraryChoices = choices.Take(3).ToArray(); var folderChoices = choices.Skip(3).Where(f => !HasCollapsedAncestor(f.Id)).ToArray();
            if (navigation.ItemsSource is not FolderChoice[] currentLibrary || !currentLibrary.SequenceEqual(libraryChoices)) navigation.ItemsSource = libraryChoices;
            navigation.SelectedItem = navigation.ItemsSource.Cast<FolderChoice>().FirstOrDefault(f => f.View == view);
            if (folders.ItemsSource is not FolderChoice[] currentFolders || !currentFolders.SequenceEqual(folderChoices)) folders.ItemsSource = folderChoices;
            folders.SelectedItem = folders.ItemsSource.Cast<FolderChoice>().FirstOrDefault(f => f.Id == selectedFolder && view == "folder");
            var visible = all.Where(j => j.State != JobState.Purged && j.Trashed == (view == "trash") && (view is "all" or "trash" || (view == "unfiled" ? j.FolderId == null : j.FolderId == selectedFolder)));
            var query = search.Text?.Trim() ?? ""; var list = visible.Where(j => Matches(j, query)).Select(j => new JobChoice(j)).ToArray();
            jobs.ItemsSource = list;
            if (list.FirstOrDefault(x => x.Job.Id == primary) is { } previous) jobs.SelectedItem = previous;
            foreach (var item in list.Where(x => selected.Contains(x.Job.Id))) if (jobs.SelectedItems?.Contains(item) == false) jobs.SelectedItems.Add(item);
            listTitle.Text = choices.FirstOrDefault(f => f.Id == selectedFolder && f.View == view)?.Label ?? "전체 녹음"; listCount.Text = list.Length.ToString();
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
        var all = store.Jobs.Where(j => j.State != JobState.Purged).ToArray();
        var result = new List<FolderChoice> { new(null, "전체 녹음", "all", Count: all.Count(j => !j.Trashed)), new(null, "미분류", "unfiled", Count: all.Count(j => !j.Trashed && j.FolderId == null)), new(null, "휴지통", "trash", Count: all.Count(j => j.Trashed)) };
        void Add(string? parent, int depth) { foreach (var f in store.Folders.Where(f => f.ParentId == parent).OrderBy(f => f.Name)) { result.Add(new(f.Id, f.Name, Depth: depth, Count: all.Count(j => !j.Trashed && j.FolderId == f.Id))); Add(f.Id, depth + 1); } }
        Add(null, 0); return result;
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
        var j = Current(); var count = Selected().Count(); selectionInfo.Text = count > 0 ? $"{count}개 선택 · Ctrl/Shift" : "최근 녹음순";
        detailBody.IsVisible = j != null; emptyDetail.IsVisible = j == null; UpdateActions();
        if (j == null) { player.SetSource(null); processingProgress.Update(null, JobState.Queued); detailId = null; editing = titleEditing = false; title.IsReadOnly = true; return; }
        player.SetSource(j.Trashed ? null : store.AudioPath(j.Id));
        if (detailId != j.Id || !editing)
        {
            refreshing = true; titleEditing = false; title.IsReadOnly = true; title.Text = RecordingTitles.Display(j); date.Text = RecordingTitles.Day(j) ?? ""; refreshing = false; editing = false; detailId = j.Id;
        }
        recordedDate.Text = j.RecordedAt != null && RecordingTitles.Day(j) == j.RecordedAt[..10] ? DisplayDate(j.RecordedAt) : RecordingTitles.Day(j)?.Replace("-", ".") ?? "녹음 날짜 미확인";
        if (dateEditor != null) dateEditor.IsVisible = RecordingTitles.Day(j) == null && j.Mobile == null;
        date.IsReadOnly = j.Mobile != null;
        UpdateProgress();
        ToolTip.SetTip(date, j.Mobile != null ? "모바일 녹음 날짜는 원본에서 유지되며 수정할 수 없습니다." : "예: 2026-10-18 · Enter 저장 / Escape 취소");
        detailPath.Text = FolderPath(j.FolderId) + (j.Trashed ? " · 휴지통" : "");
        detailState.Text = StateLabel(j.State) + " · " + j.Models.WhisperProfile + " / " + j.Models.NotesProfile + " · " + j.Models.Compute;
        var messages = new List<string>();
        if (j.Error != null) messages.Add("처리: " + j.Error); if (j.ExportError != null) messages.Add("ZIP 저장: " + j.ExportError);
        if (j.State != JobState.Completed && j.ArtifactRevision != null) messages.Add("이전 완료 결과는 유지됩니다.");
        jobInfo.IsVisible = messages.Count > 0; jobInfo.Text = string.Join("\n", messages); jobInfo.Foreground = j.Error != null || j.ExportError != null ? NativeWhite.Danger : NativeWhite.Secondary;
        try
        {
            var source = store.ReadTranscript(j.Id); var markdown = store.ReadNotes(j.Id);
            transcriptCards.SetDocument(store.ReadTranscriptDocument(j.Id));
            if (transcript.Text != source) transcript.Text = source; if (notes.Text != markdown) notes.Text = markdown; RenderNotes(markdown); UpdateProgress();
            transcript.PlaceholderText = "아직 전사가 없습니다. 처리 상태와 로컬 모델 설정을 확인하세요.";
            notes.PlaceholderText = "아직 노트가 없습니다. 전사가 있으면 재시도로 이어서 처리할 수 있습니다.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { transcript.Text = ""; transcriptCards.SetDocument(new TranscriptDocument("", [])); notes.Text = ""; jobInfo.Text += "\n결과 읽기 실패: " + e.Message; jobInfo.Foreground = NativeWhite.Danger; }
        UpdateActions();
    }
    async Task Import()
    {
        if (importing) return; importing = true; UpdateActions();
        try
        {
            var destination = selectedFolder;
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "완료된 오디오 파일 가져오기", AllowMultiple = true }).WaitAsync(work.Token); work.Token.ThrowIfCancellationRequested();
            foreach (var file in files) { var path = file.TryGetLocalPath(); if (path == null) throw new IOException("로컬 파일을 선택하세요."); await Task.Run(() => store.Import(path, folderId: destination), work.Token); }
            if (files.Count > 0) status.Text = $"{files.Count}개 가져옴 · 원본은 유지됩니다. 녹음 날짜는 확인 후 입력하세요.";
        }
        finally { importing = false; UpdateActions(); }
    }
    Task RegenerateTitle()
    {
        var job = Current();
        if (job != null)
        {
            status.Text = store.RegenerateTitle(job.Id) ? "저장된 노트로 제목을 만들었습니다." : "노트에 구체적인 주제 제목이 없습니다. 제목을 직접 수정하세요.";
            detailId = null;
        }
        return Task.CompletedTask;
    }
    Task SaveTitle()
    {
        var j = Current();
        if (j != null)
        {
            var draft = titleEditing ? title.Text ?? "" : RecordingTitles.BaseTitle(j.Title);
            store.UpdateTitleAndDate(j.Id, draft, string.IsNullOrWhiteSpace(date.Text) ? null : date.Text.Trim(), updateTitle: titleEditing);
            editing = titleEditing = false; title.IsReadOnly = true; status.Text = "제목과 녹음 날짜를 저장했습니다.";
        }
        return Task.CompletedTask;
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
        var ids = Selected().ToArray(); var destination = await ChooseFolder(); work.Token.ThrowIfCancellationRequested(); await MoveRecordings(ids, destination);
    }
    async Task MoveFolder() { var id = selectedFolder ?? throw new InvalidOperationException("폴더를 먼저 선택하세요."); var destination = await ChooseFolder(id); work.Token.ThrowIfCancellationRequested(); store.MoveFolder(id, destination); }
    async Task CreateFolder()
    {
        var parent = selectedFolder; var input = new TextBox { PlaceholderText = "폴더 이름" }; NativeWhite.Label(input, "새 폴더 이름");
        var dialog = Dialog("새 폴더", 420, 260); var stack = new StackPanel { Margin = new Thickness(24), Spacing = 14 }; stack.Children.Add(NativeWhite.Text("새 폴더", "heading")); stack.Children.Add(input); var error = NativeWhite.Text("", "error"); stack.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(NativeWhite.Action("취소", () => dialog.Close()));
        var ok = NativeWhite.Action("만들기", () =>
        {
            LibraryFolder created;
            try { work.Token.ThrowIfCancellationRequested(); created = store.CreateFolder(input.Text ?? "", parent); }
            catch (Exception e) { error.Text = e.Message; return; }
            status.Text = $"폴더 ‘{created.Name}’을 만들었습니다.";
            dialog.Close();
        }, "primary"); ok.IsDefault = true; buttons.Children.Add(ok); stack.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = stack }; dialog.Opened += (_, _) => input.Focus(); await dialog.ShowDialog(this).WaitAsync(work.Token);
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
