using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Vemo.Core;

namespace Vemo.App;

public sealed partial class MainWindow
{
    readonly StackPanel renderedNotes = new() { Spacing = 12, Margin = new Thickness(0, 16) };
    string renderedMarkdown = "";

    void RenderNotes(string markdown)
    {
        if (markdown == renderedMarkdown && renderedNotes.Children.Count > 0) return;
        renderedMarkdown = markdown; renderedNotes.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            var empty = NativeWhite.Text("아직 노트가 없습니다.\n처리 상태와 모델 설정을 확인해 주세요.", "secondary");
            empty.TextAlignment = TextAlignment.Center; empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(24, 48); renderedNotes.Children.Add(empty); return;
        }
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var heading = line.StartsWith("### ") ? 3 : line.StartsWith("## ") ? 2 : line.StartsWith("# ") ? 1 : 0;
            var text = heading > 0 ? line[(heading + 1)..] : line.StartsWith("- ") ? "•  " + line[2..] : line;
            var block = NativeWhite.Text(text, line.StartsWith("출처:") ? "caption" : null);
            block.FontSize = heading switch { 1 => 23, 2 => 19, 3 => 15, _ => 15 };
            if (heading > 0) { block.FontWeight = FontWeight.SemiBold; block.Margin = new Thickness(0, heading == 1 ? 0 : 14, 0, 2); }
            renderedNotes.Children.Add(block);
        }
    }

    string SelectedResultText()
    {
        var j = Current(); if (j == null) return "";
        return tabs.SelectedIndex == 1 ? store.ReadNotes(j.Id) : store.ReadTranscript(j.Id);
    }
    bool HasResult() => Current() != null && !string.IsNullOrWhiteSpace(tabs.SelectedIndex == 1 ? notes.Text : transcript.Text);
    async Task CopyResult()
    {
        var text = SelectedResultText(); if (string.IsNullOrWhiteSpace(text)) return;
        if (Clipboard == null) throw new IOException("클립보드를 사용할 수 없습니다.");
        await Clipboard.SetTextAsync(text); status.Text = "선택한 결과를 복사했습니다.";
    }
    async Task SaveResult()
    {
        var j = Current(); if (j == null) return;
        var text = SelectedResultText(); if (string.IsNullOrWhiteSpace(text)) return;
        var isNote = tabs.SelectedIndex == 1;
        var file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = isNote ? "노트 내보내기" : "전사 내보내기",
            SuggestedFileName = SafeFiles.FileTitle(j.Title) + (isNote ? ".md" : ".txt"),
            DefaultExtension = isNote ? "md" : "txt"
        });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), work.Token);
        status.Text = "결과 파일을 저장했습니다.";
    }
    Task OpenResultDirectory()
    {
        var j = Current();
        if (j?.ExportPath == null || !File.Exists(j.ExportPath)) throw new IOException("먼저 ZIP을 저장해 주세요.");
        var path = Path.GetDirectoryName(j.ExportPath)!;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
