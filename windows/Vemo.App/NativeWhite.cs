using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
namespace Vemo.App;

internal static class NativeWhite
{
    public static readonly IBrush Sidebar = Brush.Parse("#F7F7FA"), Divider = Brush.Parse("#E8E8ED"), Accent = Brush.Parse("#6D5BB5"), Secondary = Brush.Parse("#6B6D78"), Danger = Brush.Parse("#B5444D");
    public static TextBlock Text(string text, string? style = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style != null) block.Classes.Add(style);
        return block;
    }
    public static void Label(Control control, string name, string? help = null)
    {
        AutomationProperties.SetName(control, name);
        if (help != null) AutomationProperties.SetHelpText(control, help);
    }
    public static TextBlock SelectionMark()
    {
        var mark = new TextBlock { Text = "✓", Width = 20, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Accent, IsHitTestVisible = false };
        mark.Classes.Add("selection-check");
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        return mark;
    }
    public static Border Panel(Control child, bool rightLine = false) => new()
    {
        Child = child, BorderBrush = Divider, BorderThickness = rightLine ? new Thickness(0, 0, 1, 0) : new Thickness(0)
    };
    public static Button Action(string label, Action action, string? style = null)
    {
        var button = new Button { Content = label };
        if (style != null) button.Classes.Add(style);
        button.Click += (_, _) => action();
        return button;
    }
}
