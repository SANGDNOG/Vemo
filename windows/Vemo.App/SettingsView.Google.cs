using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Vemo.Core;
namespace Vemo.App;

public sealed partial class SettingsView
{
    Control BuildGoogleSettings(GoogleConnectionService google, TextBlock message)
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
        var folders = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "폴더를 선택하세요" };
        NativeWhite.Label(folders, "Vemo에서 접근 가능한 폴더 목록"); foldersPanel.Children.Add(folders);
        var folderName = new TextBox { PlaceholderText = "새 Vemo 폴더 이름", MaxLength = 120 };
        NativeWhite.Label(folderName, "새 Google Drive 폴더 이름"); foldersPanel.Children.Add(folderName);
        var folderActions = new WrapPanel(); foldersPanel.Children.Add(folderActions);
        var create = new Button { Content = "Drive 폴더 만들기", Margin = new Thickness(0, 0, 8, 8) };
        var select = new Button { Content = "이 폴더 사용", Margin = new Thickness(0, 0, 8, 8) }; select.Classes.Add("primary");
        var cancelFolder = new Button { Content = "폴더 선택 취소", Margin = new Thickness(0, 0, 8, 8) };
        foreach (var button in new[] { create, select, cancelFolder }) { NativeWhite.Label(button, button.Content!.ToString()!); folderActions.Children.Add(button); }

        void Refresh()
        {
            if (closed) return;
            var state = google.Status; var idle = !busy && !state.Busy;
            status.Text = state.Busy ? "Google 작업 중…" : state.Connected ? "Google 연결됨" : state.Configured ? "연결되지 않음" : "이 빌드에서는 Google 연결을 사용할 수 없습니다.";
            if (!string.IsNullOrEmpty(state.LastError)) status.Text += " · 연결 오류: " + state.LastError;
            folderStatus.Text = state.FolderId == null ? "선택한 Drive 폴더 없음" : "Drive 폴더 · " + (state.FolderName ?? state.FolderId);
            if (state.LastSync != null) folderStatus.Text += " · 마지막 동기화 " + state.LastSync.Value.ToLocalTime().ToString("g");
            connect.IsVisible = !state.Connected; connect.IsEnabled = idle && state.Configured && !state.Connected;
            folderStatus.IsVisible = state.Connected;
            syncActions.IsVisible = state.Connected;
            list.Content = state.FolderId == null ? "폴더 선택" : "폴더 변경";
            list.IsEnabled = idle && state.Connected; sync.IsEnabled = idle && state.Connected && state.FolderId != null;
            disconnect.IsVisible = state.Connected; disconnect.IsEnabled = idle && state.Connected;
            if (!state.Connected) foldersPanel.IsVisible = false;
            cancelOperation.IsVisible = busy && googleOperation; cancelOperation.IsEnabled = busy && googleOperation;
            folders.IsEnabled = idle; folderName.IsEnabled = idle;
            create.IsEnabled = idle && state.Connected && !string.IsNullOrWhiteSpace(folderName.Text);
            select.IsEnabled = idle && state.Connected && folders.SelectedItem is ComboBoxItem;
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
            if (closed) return;
            folders.ItemsSource = items.Select(f => new ComboBoxItem { Content = f.Name, Tag = f }).ToArray();
            folders.SelectedIndex = -1; foldersPanel.IsVisible = true;
            message.Foreground = NativeWhite.Secondary;
            message.Text = items.Count == 0 ? "Vemo에서 접근 가능한 폴더가 없습니다. 새 폴더를 만들 수 있습니다." : "폴더를 고른 뒤 ‘이 폴더 사용’을 누르세요.";
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            if (closed || busy || google.Status.Busy) return;
            googleOperation = true;
            try { await RunOperation(action, message); }
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
            if (folders.SelectedItem is not ComboBoxItem { Tag: GoogleDriveFolder folder }) return;
            await google.SelectFolderAsync(folder.Id, token);
            if (!closed) { foldersPanel.IsVisible = false; message.Foreground = NativeWhite.Secondary; message.Text = "Google Drive 폴더를 연결했습니다."; }
        });
        cancelFolder.Click += (_, _) => { if (closed || busy) return; foldersPanel.IsVisible = false; folders.ItemsSource = null; folderName.Text = ""; };
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
}
