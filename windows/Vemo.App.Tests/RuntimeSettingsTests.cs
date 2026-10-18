using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;
using static Vemo.App.Tests.UiFixture;
namespace Vemo.App.Tests;

public sealed class RuntimeSettingsTests
{
    [AvaloniaFact]
    public async Task Default_auto_and_explicit_cpu_are_visible_and_save_without_cuda_confirmation()
    {
        await using var ui = new UiFixture(seedRecordings: false);
        Click(ui.Window, Button(ui.Window, "설정"));
        var compute = Named<ComboBox>(ui.Window, "처리 장치");
        Assert.Equal("Auto", compute.SelectedItem);
        Assert.Equal(new[] { "Auto", "CPU", "CUDA" }, compute.Items.Cast<string>());
        Assert.NotNull(Named<TextBlock>(ui.Window, "GPU 준비 상태"));
        compute.BringIntoView(); Pump(); Assert.True(compute.Focus()); Key(ui.Window, Avalonia.Input.Key.Space); Key(ui.Window, Avalonia.Input.Key.Home); Key(ui.Window, Avalonia.Input.Key.Down); Key(ui.Window, Avalonia.Input.Key.Enter);
        Assert.Equal("CPU", compute.SelectedItem);
        var save = Button(ui.Window, "설정 저장"); save.BringIntoView(); Pump(); Click(ui.Window, save);
        Assert.False(SettingsTestInput.IsOpen(ui)); Assert.Equal("CPU", ui.Store.Settings.Models.ComputePreference); Assert.Equal("CPU", ui.Store.Settings.Models.Compute); Assert.False(ui.Store.Settings.Models.AutoGpuLayers);
    }
    [AvaloniaFact]
    public async Task Explicit_cuda_choice_saves_as_preference_without_manual_approval_checkbox()
    {
        await using var ui = new UiFixture(seedRecordings: false);
        Click(ui.Window, Button(ui.Window, "설정")); var compute = Named<ComboBox>(ui.Window, "처리 장치");
        compute.BringIntoView(); Pump(); Assert.True(compute.Focus()); Key(ui.Window, Avalonia.Input.Key.Space); Key(ui.Window, Avalonia.Input.Key.End); Key(ui.Window, Avalonia.Input.Key.Enter);
        Assert.Equal("CUDA", compute.SelectedItem);
        Assert.DoesNotContain(ui.Window.GetVisualDescendants().OfType<CheckBox>(), x => x.Content?.ToString()?.Contains("NVIDIA") == true);
        var save = Button(ui.Window, "설정 저장"); save.BringIntoView(); Pump(); Click(ui.Window, save);
        Assert.False(SettingsTestInput.IsOpen(ui)); Assert.Equal("CUDA", ui.Store.Settings.Models.ComputePreference);
        Assert.False(ui.Store.Settings.Models.CudaConfirmed); Assert.Equal("CPU", ui.Store.Settings.Models.Compute);
    }
}
