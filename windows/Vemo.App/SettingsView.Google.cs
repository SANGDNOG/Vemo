using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Vemo.Core;
namespace Vemo.App;

public sealed partial class SettingsView
{
    Control BuildGoogleSettings(GoogleConnectionService google, TextBlock message, string processorId)
    {
        var section = new StackPanel { Spacing = 10 };
        var label = NativeWhite.Text("Google Drive", "secondary"); label.FontSize = 11; label.FontWeight = Avalonia.Media.FontWeight.Medium; section.Children.Add(label);
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        var card = new Border { Child = body }; card.Classes.Add("settings-group"); section.Children.Add(card);
        var status = NativeWhite.Text("", "secondary"); NativeWhite.Label(status, "Google 연결 상태"); body.Children.Add(status);
        var folderStatus = NativeWhite.Text("", "caption"); NativeWhite.Label(folderStatus, "Google 선택한 폴더"); body.Children.Add(folderStatus);
        var googleOperation = false;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal }; body.Children.Add(actions);
        Button Action(string label)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            button.Classes.Add("compact"); NativeWhite.Label(button, label); actions.Children.Add(button); return button;
        }
        var connect = Action("Google Drive 연결"); connect.Classes.Add("primary");
        var list = Action("폴더 선택");
        var sync = Action("동기화");
        var disconnect = Action("연결 해제");
        var cancelOperation = Action("Google 작업 취소");
        NativeWhite.Label(connect, "Google 로그인"); NativeWhite.Label(list, "Drive 폴더 선택…");
        NativeWhite.Label(sync, "지금 동기화"); NativeWhite.Label(disconnect, "Google 연결 해제");
        actions.Children.Remove(list); actions.Children.Remove(sync);
        var syncActions = new WrapPanel(); syncActions.Children.Add(list); syncActions.Children.Add(sync); body.Children.Add(syncActions);
        var foldersPanel = new StackPanel { Spacing = 10, IsVisible = false }; body.Children.Add(foldersPanel);
        foldersPanel.Children.Add(NativeWhite.Text("Vemo에서 접근 가능한 폴더", "secondary"));
        var emptyFolders = NativeWhite.Text("이 PC에 연결할 폴더를 만들어 주세요.", "secondary"); emptyFolders.IsVisible = false; foldersPanel.Children.Add(emptyFolders);
        var folders = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "폴더를 선택하세요" };
        NativeWhite.Label(folders, "Vemo에서 접근 가능한 폴더 목록"); foldersPanel.Children.Add(folders);
        var folderName = new TextBox { Text = "Vemo Windows", PlaceholderText = "새 Vemo 폴더 이름", MaxLength = 120 };
        NativeWhite.Label(folderName, "새 Google Drive 폴더 이름"); foldersPanel.Children.Add(folderName);
        var folderActions = new WrapPanel(); foldersPanel.Children.Add(folderActions);
        var create = new Button { Content = "Drive 폴더 만들기", Margin = new Thickness(0, 0, 8, 8) };
        create.Classes.Add("primary");
        var select = new Button { Content = "이 폴더 사용", Margin = new Thickness(0, 0, 8, 8) }; select.Classes.Add("primary");
        var cancelFolder = new Button { Content = "폴더 선택 취소", Margin = new Thickness(0, 0, 8, 8) };
        foreach (var button in new[] { create, select, cancelFolder }) { NativeWhite.Label(button, button.Content!.ToString()!); folderActions.Children.Add(button); }

        void Refresh()
        {
            if (closed) return;
            var state = google.Status; var idle = !busy && !state.Busy;
            status.Text = state.Busy ? "Google 작업 중…" : state.Connected ? "Google 연결됨" : state.Configured ? "연결되지 않음" : "이 빌드에서는 Google 연결을 사용할 수 없습니다.";
            if (!string.IsNullOrEmpty(state.LastError)) status.Text += " · " + GoogleError(state.LastError);
            folderStatus.Text = state.FolderId == null ? "선택한 Drive 폴더 없음" : "Drive 폴더 · " + (state.FolderName ?? state.FolderId);
            if (state.LastSync != null) folderStatus.Text += " · 마지막 동기화 " + state.LastSync.Value.ToLocalTime().ToString("g");
            connect.IsVisible = !state.Connected; connect.IsEnabled = idle && state.Configured && !state.Connected;
            folderStatus.IsVisible = state.Connected;
            syncActions.IsVisible = state.Connected;
            list.IsVisible = state.FolderId == null;
            list.IsEnabled = idle && state.Connected && state.FolderId == null; sync.IsEnabled = idle && state.Connected && state.FolderId != null;
            disconnect.IsVisible = state.Connected; disconnect.IsEnabled = idle && state.Connected;
            if (!state.Connected || state.FolderId != null) foldersPanel.IsVisible = false;
            cancelOperation.IsVisible = busy && googleOperation; cancelOperation.IsEnabled = busy && googleOperation;
            folders.IsEnabled = idle; folderName.IsEnabled = idle;
            create.IsEnabled = idle && state.Connected && state.FolderId == null && !string.IsNullOrWhiteSpace(folderName.Text);
            select.IsEnabled = idle && state.Connected && state.FolderId == null && folders.SelectedItem is ComboBoxItem;
            cancelFolder.IsEnabled = idle;
        }
        void Changed(object? sender, EventArgs e)
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(Refresh);
        }
        google.StatusChanged += Changed;
        releaseGoogle = () => google.StatusChanged -= Changed;
        BusyChanged += Refresh; folders.SelectionChanged += (_, _) => Refresh(); folderName.TextChanged += (_, _) => Refresh();
        void ShowFolders(IReadOnlyList<GoogleDriveFolder> items)
        {
            if (closed || google.Status.FolderId != null) return;
            var eligible = items.Where(f => f.ProcessorId == processorId).ToArray();
            emptyFolders.IsVisible = eligible.Length == 0; folders.IsVisible = select.IsVisible = eligible.Length > 0;
            if (string.IsNullOrWhiteSpace(folderName.Text)) folderName.Text = "Vemo Windows";
            folders.ItemsSource = eligible.Select(f => new ComboBoxItem { Content = f.Name, Tag = f }).ToArray();
            folders.SelectedIndex = -1; foldersPanel.IsVisible = true;
            message.Foreground = NativeWhite.Secondary;
            message.Text = eligible.Length == 0 ? "이 PC에 연결할 폴더를 만들어 주세요." : "폴더를 고른 뒤 ‘이 폴더 사용’을 누르세요.";
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            if (closed || busy || google.Status.Busy) return;
            googleOperation = true;
            try
            {
                await RunOperation(async token =>
                {
                    try { await action(token); }
                    catch (GoogleConnectionException error) { throw new InvalidOperationException(GoogleError(error.Code), error); }
                    catch (Exception error) when (error is not OperationCanceledException) { throw new InvalidOperationException(GoogleError(null), error); }
                }, message);
            }
            finally { googleOperation = false; if (!closed) Refresh(); }
        }
        connect.Click += async (_, _) => await Run(async token =>
        {
            message.Foreground = NativeWhite.Secondary; message.Text = "브라우저에서 Google 로그인을 완료하세요.";
            await google.ConnectAsync(token);
            token.ThrowIfCancellationRequested();
            if (closed) return;
            if (google.Status.Connected && google.Status.FolderId == null)
                ShowFolders(await google.ListFoldersAsync(token));
            else if (google.Status.Connected) message.Text = "Google에 연결했습니다.";
        });
        list.Click += async (_, _) => await Run(async token => ShowFolders(await google.ListFoldersAsync(token)));
        create.Click += async (_, _) => await Run(async token =>
        {
            if (google.Status.FolderId != null) return;
            var folder = await google.CreateFolderAsync(folderName.Text?.Trim() ?? "", token);
            if (closed || token.IsCancellationRequested) return;
            folderName.Text = "";
            if (google.Status.FolderId == folder.Id)
            {
                foldersPanel.IsVisible = false; folders.ItemsSource = null;
                message.Foreground = NativeWhite.Secondary; message.Text = "Google Drive 폴더를 만들고 연결했습니다.";
            }
            else
            {
                ShowFolders([folder]); folders.SelectedIndex = 0;
                message.Text = "폴더를 만들었습니다. ‘이 폴더 사용’을 눌러 연결하세요.";
            }
        });
        select.Click += async (_, _) => await Run(async token =>
        {
            if (google.Status.FolderId != null || folders.SelectedItem is not ComboBoxItem { Tag: GoogleDriveFolder folder } || folder.ProcessorId != processorId) return;
            await google.SelectFolderAsync(folder.Id, token);
            if (!closed) { foldersPanel.IsVisible = false; message.Foreground = NativeWhite.Secondary; message.Text = "Google Drive 폴더를 연결했습니다."; }
        });
        cancelFolder.Click += (_, _) => { if (closed || busy) return; foldersPanel.IsVisible = false; folders.ItemsSource = null; folderName.Text = "Vemo Windows"; };
        cancelOperation.Click += (_, _) => operation?.Cancel();
        disconnect.Click += async (_, _) => await Run(async token =>
        {
            await google.DisconnectAsync(token);
            if (!closed) { foldersPanel.IsVisible = false; folders.ItemsSource = null; message.Foreground = NativeWhite.Secondary; message.Text = "Google 연결을 해제했습니다."; }
        });
        sync.Click += async (_, _) => await Run(async token =>
        {
            var result = await google.SyncAsync(token);
            if (!closed) { message.Foreground = NativeWhite.Secondary; message.Text = $"Google 동기화 완료 · {result.Imported}개 가져옴 · {result.Published}개 게시 · {result.Skipped}개 건너뜀"; }
        });
        Refresh(); return section;
    }

    static string GoogleError(string? code) => code switch
    {
        "cancelled" => "작업을 취소했습니다.",
        "processor_mismatch" => "다른 PC에 연결된 폴더입니다. 이 PC용 폴더를 만들어 주세요.",
        "binding_conflict" => "이미 연결된 폴더가 있습니다. 현재 폴더를 사용해 주세요.",
        "account_mismatch" => "폴더에 연결된 Google 계정으로 다시 로그인해 주세요.",
        "reauthorization_required" or "not_connected" => "Google에 다시 로그인해 주세요.",
        "access_denied" => "Google 연결을 허용하지 않았습니다. 다시 연결할 수 있습니다.",
        "folder_required" or "folder_not_accessible" or "not_found" => "연결할 폴더를 확인해 주세요.",
        "duplicate_roots" => "같은 PC에 연결된 폴더가 여러 개입니다. Drive 폴더를 확인해 주세요.",
        "configuration_required" or "invalid_configuration" => "이 빌드에서는 Google 연결을 사용할 수 없습니다.",
        "transport_failed" or "drive_request_failed" => "Google에 연결하지 못했습니다. 네트워크를 확인하고 다시 시도해 주세요.",
        "remote_conflict" => "Drive의 내용이 변경되었습니다. 다시 동기화해 주세요.",
        _ => "Google 작업을 완료하지 못했습니다. 다시 시도해 주세요."
    };
}
