using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class NotesModelSettingsTests
{
    [AvaloniaTheory]
    [InlineData(@"C:\models\Qwen3.5-9B-Q4_K_M.gguf", "Qwen3.5 9B GGUF", "Qwen3.5-9B-Q4_K_M.gguf")]
    [InlineData("/models/qwen3.5-27b-q4_k_m.GGUF", "Qwen3.5 27B GGUF", "qwen3.5-27b-q4_k_m.GGUF")]
    [InlineData("/models/Qwen3.5-9B.gguf", "Qwen3.5 9B GGUF", "Qwen3.5-9B.gguf")]
    [InlineData("/models/custom.gguf", "사용자 지정 GGUF", "custom.gguf")]
    [InlineData("/models/Qwen3.5-9B-extra-27B.gguf", "Qwen3.5 9B GGUF", "Qwen3.5-9B-extra-27B.gguf")]
    [InlineData("/models/Qwen3.5-9Billion.gguf", "사용자 지정 GGUF", "Qwen3.5-9Billion.gguf")]
    [InlineData("", "모델 파일 미선택", "GGUF 파일을 선택하세요")]
    public async Task Actual_file_controls_display_and_saved_profile_without_replacing_model(string path, string expectedProfile, string expectedFilename)
    {
        await using var ui = new UiFixture(seedRecordings: false, configure: store =>
            store.UpdateSettings(store.Settings with { Models = store.Settings.Models with { NotesProfile = "Qwen3.5 27B GGUF", LlamaModel = path } }));
        Click(ui.Window, Button(ui.Window, "설정"));
        Assert.Equal(expectedProfile, Named<TextBlock>(ui.Window, "노트 모델").Text);
        var filename = Named<TextBlock>(ui.Window, "사용할 노트 모델 파일");
        Assert.Equal(expectedFilename, filename.Text);
        Assert.True(filename.IsEffectivelyVisible);
        Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<ComboBox>(), x => AutomationProperties.GetName(x) == "노트 모델");
        var advanced = Assert.Single(ui.Window.GetVisualDescendants().OfType<Expander>(), x => Equals(x.Header, "고급 · 실행 파일과 모델 경로"));
        Assert.False(advanced.IsExpanded);
        var before = ui.Store.Settings.Models;
        var save = Button(ui.Window, "설정 저장"); save.BringIntoView(); Pump(); Click(ui.Window, save);
        Assert.False(SettingsTestInput.IsOpen(ui));
        Assert.Equal(before with { NotesProfile = expectedProfile }, ui.Store.Settings.Models);
        Click(ui.Window, Button(ui.Window, "설정"));
        Assert.Equal(expectedProfile, Named<TextBlock>(ui.Window, "노트 모델").Text);
        Assert.Equal(expectedFilename, Named<TextBlock>(ui.Window, "사용할 노트 모델 파일").Text);
    }

    [AvaloniaFact]
    public async Task Editing_actual_file_updates_display_and_save_ignores_stale_profile()
    {
        await using var ui = new UiFixture(seedRecordings: false);
        Click(ui.Window, Button(ui.Window, "설정"));
        SettingsTestInput.ShowAdvanced(ui);
        var model = Named<TextBox>(ui.Window, "Qwen GGUF 모델 파일");
        foreach (var (path, profile) in new[] {
            (@"C:\models\Qwen3.5-27B-Q4_K_M.gguf", "Qwen3.5 27B GGUF"),
            (@"C:\models\custom.gguf", "사용자 지정 GGUF"),
            (@"C:\models\Qwen3.5-9B-Q4_K_M.gguf", "Qwen3.5 9B GGUF") })
        {
            SettingsTestInput.Edit(ui, model, path);
            Assert.Equal(profile, Named<TextBlock>(ui.Window, "노트 모델").Text);
            Assert.Equal(path.Split('\\')[^1], Named<TextBlock>(ui.Window, "사용할 노트 모델 파일").Text);
        }
        var save = Button(ui.Window, "설정 저장"); save.BringIntoView(); Pump(); Click(ui.Window, save);
        Assert.False(SettingsTestInput.IsOpen(ui));
        Assert.Equal("Qwen3.5 9B GGUF", ui.Store.Settings.Models.NotesProfile);
        Assert.Equal(@"C:\models\Qwen3.5-9B-Q4_K_M.gguf", ui.Store.Settings.Models.LlamaModel);
    }
}
