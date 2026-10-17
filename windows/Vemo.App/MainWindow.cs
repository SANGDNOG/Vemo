using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Vemo.Core;
namespace Vemo.App;

public sealed class MainWindow : Window
{
    readonly LibraryStore store; readonly JobRunner runner; readonly MobileFolderAdapter mobile;
    readonly ListBox folders = new(), jobs = new() { SelectionMode = SelectionMode.Multiple };
    readonly TextBox title = new(), date = new() { PlaceholderText = "Unknown date or ISO 8601 with timezone" }, transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, notes = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap }, jobInfo = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    readonly CheckBox trashView = new() { Content = "Trash" }; readonly WindowWork work = new(); readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    string? selectedFolder; bool refreshing; bool closing; bool closeReady; Task backgroundTask = Task.CompletedTask; string lastSignature = "";
    record FolderChoice(string? Id, string Label) { public override string ToString() => Label; }
    record JobChoice(RecordingJob Job) { public override string ToString() => $"{Job.Title}\n{Job.RecordedAt ?? "Unknown recording date"} · {Job.State}{(Job.Trashed ? " · Trash" : "")}"; }
    public MainWindow()
    {
        Title = "Vemo — Windows functional preview"; Width = 1250; Height = 800; MinWidth = 900; MinHeight = 600;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Vemo.App/Assets/vemo-icon.png")));
        store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vemo", "WindowsPreview")); runner = new(store, new LocalCliInferenceEngine()); mobile = new(store);
        var outer = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(12) };
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(Button("Import audio", Import)); toolbar.Children.Add(Button("Settings", async () => await new SettingsWindow(store, mobile).ShowDialog(this))); toolbar.Children.Add(Button("New folder", CreateFolder)); toolbar.Children.Add(Button("Move folder", MoveFolder));
        toolbar.Children.Add(Button("Pause / resume queue", () => { store.UpdateSettings(store.Settings with { QueuePaused = !store.Settings.QueuePaused }); Refresh(); return Task.CompletedTask; })); toolbar.Children.Add(Button("Scan mobile folder", Scan));
        outer.Children.Add(toolbar);
        var columns = new Grid { ColumnDefinitions = new("220,330,*"), Margin = new Thickness(0, 10) }; Grid.SetRow(columns, 1); outer.Children.Add(columns);
        var left = new Grid { RowDefinitions = new("Auto,*") }; left.Children.Add(trashView); Grid.SetRow(folders, 1); left.Children.Add(folders); columns.Children.Add(left);
        Grid.SetColumn(jobs, 1); columns.Children.Add(jobs);
        var detail = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*"), Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(detail, 2); columns.Children.Add(detail);
        detail.Children.Add(title); Grid.SetRow(date, 1); detail.Children.Add(date);
        var actions = new WrapPanel(); actions.Children.Add(Button("Save title / date", SaveTitle)); actions.Children.Add(Button("Move selected", Move)); actions.Children.Add(Button("Trash", () => Action(ids => store.Trash(ids)))); actions.Children.Add(Button("Restore", () => Action(ids => store.Restore(ids)))); actions.Children.Add(Button("Purge owned copies", Purge)); actions.Children.Add(Button("Retry snapshot", () => Action(ids => store.Retry(ids)))); actions.Children.Add(Button("Retry current models", () => Action(ids => store.Retry(ids, true)))); actions.Children.Add(Button("Cancel selected", Cancel)); actions.Children.Add(Button("Export ZIP", Export)); actions.Children.Add(Button("Publish mobile result", Publish)); Grid.SetRow(actions, 2); detail.Children.Add(actions);
        Grid.SetRow(jobInfo, 3); detail.Children.Add(jobInfo);
        var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "Transcript", Content = transcript }, new TabItem { Header = "Study notes (Markdown)", Content = notes } } }; Grid.SetRow(tabs, 4); detail.Children.Add(tabs);
        Grid.SetRow(status, 2); outer.Children.Add(status); Content = outer;
        folders.SelectionChanged += (_, _) => { if (refreshing || closing) return; selectedFolder = (folders.SelectedItem as FolderChoice)?.Id; lastSignature = ""; Refresh(); }; jobs.SelectionChanged += (_, _) => Detail(); trashView.IsCheckedChanged += (_, _) => { if (closing) return; lastSignature = ""; Refresh(); };
        refresh.Tick += (_, _) => Refresh(); Opened += (_, _) => { if (closing) return; Refresh(); refresh.Start(); backgroundTask = work.Start(RunBackground); };
        Closing += (_, e) =>
        {
            if (closeReady) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            refresh.Stop();
            if (Content is Control content) content.IsEnabled = false;
            status.Text = "Cancelling and finishing background work before closing…";
            _ = CloseAfterDrain();
        };
        Closed += (_, _) => { store.Dispose(); work.Dispose(); };
    }
    async Task CloseAfterDrain()
    {
        // Stop registration and cancel the lifetime before any new queue step can start.
        var drained = work.StopAsync();
        runner.CancelActive();
        foreach (var owned in OwnedWindows.ToArray()) owned.Close();
        try { await drained; } catch (Exception e) { status.Text = "Background stopped: " + e.Message; }
        // Also observe the tracked background task if it faulted before shutdown.
        try { await backgroundTask; } catch (OperationCanceledException) { } catch (Exception e) { status.Text = "Background stopped: " + e.Message; }
        closeReady = true;
        Close();
    }
    Button Button(string text, Func<Task> action)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        b.Click += async (_, _) =>
        {
            if (closing) return;
            try { await work.Start(_ => action()); if (!closing) { lastSignature = ""; Refresh(); } }
            catch (OperationCanceledException) when (closing) { }
            catch (Exception e) { if (!closing) status.Text = e.Message; }
        };
        return b;
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
    RecordingJob? Current() => (jobs.SelectedItem as JobChoice)?.Job;
    void Refresh()
    {
        if (closing) return;
        var folderChoices = FolderChoices(); var signature = string.Join('|', store.Jobs.Select(j => $"{j.Id}:{j.Title}:{j.FolderId}:{j.Trashed}:{j.State}:{j.Error}:{j.ExportPath}:{j.ExportError}")) + string.Join('|', folderChoices) + trashView.IsChecked + selectedFolder;
        Title = "Vemo — Windows functional preview" + (store.Settings.QueuePaused ? " (queue paused)" : ""); if (signature == lastSignature) return; lastSignature = signature;
        var selected = Selected().ToHashSet(); refreshing = true; folders.ItemsSource = folderChoices; folders.SelectedItem = folderChoices.FirstOrDefault(f => f.Id == selectedFolder);
        var list = store.Jobs.Where(j => j.State != JobState.Purged && j.Trashed == (trashView.IsChecked == true) && (trashView.IsChecked == true || selectedFolder == null || j.FolderId == selectedFolder)).Select(j => new JobChoice(j)).ToArray(); jobs.ItemsSource = list; foreach (var item in list.Where(x => selected.Contains(x.Job.Id))) jobs.SelectedItems?.Add(item); refreshing = false; Detail();
    }
    List<FolderChoice> FolderChoices() { var result = new List<FolderChoice> { new(null, "All recordings / library root") }; void Add(string? parent, string prefix) { foreach (var f in store.Folders.Where(f => f.ParentId == parent).OrderBy(f => f.Name)) { result.Add(new(f.Id, prefix + f.Name)); Add(f.Id, prefix + f.Name + " / "); } } Add(null, ""); return result; }
    void Detail() { if (refreshing || closing) return; var j = Current(); title.Text = j?.Title ?? ""; date.Text = j?.RecordedAt ?? ""; transcript.Text = j == null ? "" : store.ReadTranscript(j.Id); notes.Text = j == null ? "" : store.ReadNotes(j.Id); jobInfo.Text = j == null ? "Select a recording. Multi-select with Ctrl/Shift." : $"{j.State} · {j.Models.WhisperProfile} / {j.Models.NotesProfile} · {j.Models.Compute}\n{j.Error}\n{j.ExportError}\n{j.ExportPath}"; }
    async Task Import() { var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Import finished audio files", AllowMultiple = true }).WaitAsync(work.Token); work.Token.ThrowIfCancellationRequested(); foreach (var file in files) { var path = file.TryGetLocalPath(); if (path == null) throw new IOException("Select a local file."); var j = store.Import(path); if (selectedFolder != null) store.Move([j.Id], selectedFolder); } status.Text = $"Imported {files.Count} audio file(s). Recording date remains unknown until supplied."; }
    Task SaveTitle() { var j = Current(); if (j != null) { store.Rename(j.Id, title.Text ?? ""); store.SetRecordedAt(j.Id, string.IsNullOrWhiteSpace(date.Text) ? null : date.Text); } return Task.CompletedTask; }
    Task Action(Action<string[]> action) { action(Selected().ToArray()); return Task.CompletedTask; }
    async Task<string?> ChooseFolder() { var choices = FolderChoices(); var combo = new ComboBox { ItemsSource = choices, SelectedIndex = 0, MinWidth = 300 }; var dialog = new Window { Title = "Destination folder", Width = 440, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var stack = new StackPanel { Margin = new Thickness(16), Spacing = 10 }; stack.Children.Add(combo); var ok = new Button { Content = "Move" }; ok.Click += (_, _) => dialog.Close(true); stack.Children.Add(ok); dialog.Content = stack; return await dialog.ShowDialog<bool>(this).WaitAsync(work.Token) ? (combo.SelectedItem as FolderChoice)?.Id : throw new OperationCanceledException("Move cancelled."); }
    async Task Move() { var destination = await ChooseFolder(); work.Token.ThrowIfCancellationRequested(); store.Move(Selected(), destination); foreach (var id in Selected()) if (store.Get(id).State == JobState.Completed && !store.Get(id).Trashed) { try { store.Export(id); } catch (Exception e) { store.SetExportError(id, e.Message); } } }
    async Task MoveFolder() { if (selectedFolder == null) throw new InvalidOperationException("Select a folder first."); var destination = await ChooseFolder(); work.Token.ThrowIfCancellationRequested(); store.MoveFolder(selectedFolder, destination); }
    async Task CreateFolder() { var input = new TextBox { PlaceholderText = "Folder name" }; var dialog = new Window { Title = "New subfolder", Width = 380, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var stack = new StackPanel { Margin = new Thickness(16), Spacing = 10 }; stack.Children.Add(input); var ok = new Button { Content = "Create" }; ok.Click += (_, _) => dialog.Close(input.Text); stack.Children.Add(ok); dialog.Content = stack; var name = await dialog.ShowDialog<string?>(this); work.Token.ThrowIfCancellationRequested(); if (name != null) store.CreateFolder(name, selectedFolder); }
    Task Purge() { store.Purge(Selected()); status.Text = "Purged selected owned copies. Original imports and exported ZIPs remain."; return Task.CompletedTask; }
    Task Cancel() { var ids = Selected().ToArray(); store.CancelQueued(ids); if (runner.ActiveJobId != null && ids.Contains(runner.ActiveJobId)) runner.CancelActive(); return Task.CompletedTask; }
    async Task Export() { foreach (var id in Selected()) { work.Token.ThrowIfCancellationRequested(); await Task.Run(() => store.Export(id), work.Token); } status.Text = "ZIP export complete."; }
    async Task Publish() { foreach (var id in Selected()) { work.Token.ThrowIfCancellationRequested(); await Task.Run(() => mobile.Publish(id), work.Token); } status.Text = "Local result.json published. This is not a cloud upload receipt."; }
    async Task Scan() { var r = await Task.Run(() => mobile.ScanAsync(work.Token)); status.Text = $"Mobile: {r.Imported} imported, {r.Skipped} skipped. " + string.Join("; ", r.Issues.Select(i => i.Message).Take(3)); }
}
