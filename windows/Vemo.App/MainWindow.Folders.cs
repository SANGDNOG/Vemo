using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Vemo.App;

public sealed partial class MainWindow
{
    bool HasCollapsedAncestor(string? id)
    {
        var seen = new HashSet<string>();
        var parent = store.Folders.FirstOrDefault(f => f.Id == id)?.ParentId;
        while (parent != null && seen.Add(parent))
        {
            if (collapsedFolders.Contains(parent)) return true;
            parent = store.Folders.FirstOrDefault(f => f.Id == parent)?.ParentId;
        }
        return false;
    }

    Control[] FolderMenu() => [
        Menu("새 하위 폴더…", CreateFolder, () => selectedFolder != null),
        Menu("이름 변경…", RenameFolder, () => selectedFolder != null),
        Menu("선택 폴더 이동…", MoveFolder, () => selectedFolder != null),
        new Separator(),
        Menu("폴더 삭제…", DeleteFolder, () => selectedFolder != null, danger: true)
    ];

    async Task RenameFolder()
    {
        var id = selectedFolder;
        var folder = store.Folders.FirstOrDefault(f => f.Id == id);
        if (folder == null) return;
        var dialog = Dialog("폴더 이름 변경", 420, 260);
        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        stack.Children.Add(NativeWhite.Text("폴더 이름 변경", "heading"));
        var input = new TextBox { Text = folder.Name }; NativeWhite.Label(input, "폴더 이름"); stack.Children.Add(input);
        var error = NativeWhite.Text("", "error"); stack.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(NativeWhite.Action("취소", dialog.Close));
        var save = NativeWhite.Action("저장", () =>
        {
            try { work.Token.ThrowIfCancellationRequested(); store.RenameFolder(id!, input.Text ?? ""); dialog.Close(); }
            catch (Exception e) { error.Text = e.Message; }
        }, "primary");
        save.IsDefault = true; buttons.Children.Add(save); stack.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = stack };
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        await dialog.ShowDialog(this).WaitAsync(work.Token);
    }

    async Task DeleteFolder()
    {
        var id = selectedFolder;
        var folder = store.Folders.FirstOrDefault(f => f.Id == id);
        if (folder == null) return;
        var dialog = Dialog("폴더 삭제", 460, 280);
        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        stack.Children.Add(NativeWhite.Text($"‘{folder.Name}’ 폴더를 삭제할까요?", "heading"));
        stack.Children.Add(NativeWhite.Text("하위 폴더도 함께 삭제됩니다. 녹음과 전사·노트는 미분류에 보관되며, 원본과 이미 내보낸 ZIP은 유지됩니다.", "secondary"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = NativeWhite.Action("취소", () => dialog.Close(false)); buttons.Children.Add(cancel);
        buttons.Children.Add(NativeWhite.Action("폴더 삭제", () => dialog.Close(true), "danger"));
        stack.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = stack }; dialog.Opened += (_, _) => cancel.Focus();
        if (await dialog.ShowDialog<bool>(this).WaitAsync(work.Token))
        {
            work.Token.ThrowIfCancellationRequested(); store.DeleteFolder(id!); selectedFolder = null; view = "unfiled";
            status.Text = "폴더를 삭제했습니다. 녹음과 결과는 미분류에 보관했습니다.";
        }
    }
}
