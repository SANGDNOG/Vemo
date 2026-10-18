using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Vemo.App;

internal static class VemoIcons
{
    // Small outline icons follow the approved design's neutral folder/navigation language.
    public static Avalonia.Controls.Shapes.Path Outline(string kind) => new()
    {
        Width = 18, Height = 18, Stretch = Stretch.Uniform, Stroke = NativeWhite.Secondary,
        StrokeThickness = 1.6, IsHitTestVisible = false,
        Data = Geometry.Parse(kind switch
        {
            "all" => "M5,4 L19,4 L19,22 L5,22 Z M8,1 L16,1 M2,7 L2,19",
            "trash" => "M3,6 L21,6 M8,3 L16,3 M6,6 L7,22 L17,22 L18,6 M10,10 L10,18 M14,10 L14,18",
            "unfiled" => "M3,4 L21,4 L21,20 L3,20 Z M3,12 L8,12 L10,15 L14,15 L16,12 L21,12",
            _ => "M2,5 L9,5 L12,8 L22,8 L22,21 L2,21 Z"
        })
    };
}
