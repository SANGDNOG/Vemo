using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Xunit;
using static Vemo.App.Tests.UiFixture;

namespace Vemo.App.Tests;

internal static class SettingsTestInput
{
    public static bool IsOpen(UiFixture ui) => ui.Window.GetVisualDescendants().OfType<SettingsView>().Any();

    public static SettingsView AssertOpen(UiFixture ui)
    {
        Assert.Empty(ui.Window.OwnedWindows);
        var pane = Assert.Single(ui.Window.GetVisualDescendants().OfType<SettingsView>());
        Assert.True(pane.IsEffectivelyVisible);
        Assert.True(ui.Navigation.IsEffectivelyVisible && ui.Jobs.IsEffectivelyVisible);
        return pane;
    }

    public static void ShowAdvanced(UiFixture ui)
    {
        var expander = Assert.Single(ui.Window.GetVisualDescendants().OfType<Expander>(), x => Equals(x.Header, "고급 · 실행 파일과 모델 경로"));
        if (expander.IsExpanded) return;
        expander.BringIntoView(); Pump();
        var header = Assert.Single(expander.GetVisualDescendants().OfType<ToggleButton>(), b => b.IsEffectivelyVisible);
        Click(ui.Window, header);
        Assert.True(expander.IsExpanded);
    }

    public static void Edit(UiFixture ui, TextBox input, string value)
    {
        input.BringIntoView();
        Pump();
        input.SelectAll();
        Type(ui.Window, input, value);
        Assert.Equal(value, input.Text);
    }
}
