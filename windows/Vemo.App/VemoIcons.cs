using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Vemo.App;

internal static class VemoIcons
{
    // Preserve the prototype's 24-unit SVG viewport rather than stretching its path bounds.
    public static Control Outline(string kind, double size = 19)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var path = new Avalonia.Controls.Shapes.Path
        {
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse(kind switch
            {
                "all" => "M6 5h12v16H6z M8 2h8 M4 8h2 M18 8h2",
                "trash" => "M3 6h18 M9 6V3h6v3 M6 6l1 15h10l1-15 M10 10v7 M14 10v7",
                "unfiled" => "M3 5h18v15H3z M3 13h5l2 3h4l2-3h5",
                "settings" => "M9 3h6l1 4 4 2v6l-4 2-1 4H9l-1-4-4-2V9l4-2z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0",
                "search" => "M20 20l-5-5 M17 10a7 7 0 1 1-14 0 7 7 0 0 1 14 0",
                "more" => "M5 12h.01 M12 12h.01 M19 12h.01",
                "chevron" => "M9 5l7 7-7 7",
                "down" => "M5 9l7 7 7-7",
                "plus" => "M12 4v16 M4 12h16",
                "import" => "M12 3v12 M7 10l5 5 5-5 M4 15v6h16v-6",
                "text" => "M4 5h16 M4 10h16 M4 15h10 M4 20h13",
                "note" => "M5 3h10l4 4v14H5z M15 3v5h4 M8 12h8 M8 16h6",
                "copy" => "M8 8h12v13H8z M16 8V3H3v13h5",
                "export" => "M12 3v12 M7 8l5-5 5 5 M4 14v7h16v-7",
                _ => "M3 6h6l2 2h10v12H3z M3 6V4h6l2 2"
            })
        };
        path.Classes.Add("vemo-icon"); canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    }

    public static void Decorate(Button button, string kind, bool iconOnly = false, double size = 19)
    {
        var label = button.Content as string ?? "";
        NativeWhite.Label(button, label);
        button.ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((text, _) =>
        {
            if (iconOnly) return Outline(kind, size);
            var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(Outline(kind, size));
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            return content;
        });
        if (iconOnly) { button.Classes.Add("icon-action"); ToolTip.SetTip(button, label); }
    }
}
