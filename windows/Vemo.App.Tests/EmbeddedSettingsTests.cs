using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

public sealed class EmbeddedSettingsTests
{
    [AvaloniaFact]
    public async Task Settings_keeps_library_and_list_visible_and_closes_without_losing_selection()
    {
        await using var ui = new UiFixture();
        ui.Navigate(1); ui.SelectJob();
        var selected = ui.Jobs.SelectedIndex;
        Click(ui.Window, Button(ui.Window, "설정"));
        Assert.Empty(ui.Window.OwnedWindows);
        Assert.True(ui.Navigation.IsVisible && ui.Jobs.IsVisible);
        Assert.Contains(ui.Window.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Google Drive");
        Click(ui.Window, Button(ui.Window, "취소"));
        Assert.Equal(selected, ui.Jobs.SelectedIndex);
        Assert.True(Named<TextBox>(ui.Window, "녹음 제목").IsVisible);
    }
}
