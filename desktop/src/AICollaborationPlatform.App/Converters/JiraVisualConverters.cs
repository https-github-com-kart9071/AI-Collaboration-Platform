using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AICollaborationPlatform.App.Converters;

/// <summary>
/// Jira-style visuals: issue type icons, priority arrows and avatar colours.
/// Icons are drawn as vector paths in a 16×16 box, so they stay sharp at any DPI and don't depend on an icon font.
/// </summary>
public static class JiraVisuals
{
    public sealed record Icon(Geometry? Shape, Brush Color);

    // Issue types: white shape on a coloured rounded square, like Jira's own icons
    private static readonly Icon Bug = Make("M8,4.2 A3.8,3.8 0 1 1 7.99,4.2 Z", "#E5493A");
    private static readonly Icon Epic = Make("M9.2,2.5 L4.5,9 H7.6 L6.6,13.5 L11.5,6.6 H8.4 Z", "#904EE2");
    private static readonly Icon Story = Make("M5,3.5 H11 V13 L8,10.6 L5,13 Z", "#63BA3C");
    private static readonly Icon Subtask = Make("M3.5,3.5 H9 V9 H3.5 Z M7,7 H12.5 V12.5 H7 Z", "#4BADE8");
    private static readonly Icon Task = Make("M3.4,8.3 L4.9,6.8 L6.9,8.8 L11.1,4.6 L12.6,6.1 L6.9,11.8 Z", "#4BADE8");
    private static readonly Icon OtherType = Make("M4.5,4.5 H11.5 V11.5 H4.5 Z", "#8590A2");

    // Priorities: stroked chevrons, red pointing up for urgent, blue pointing down for minor
    private static readonly Icon Highest = Make("M3,8.5 L8,3.5 L13,8.5 M3,13 L8,8 L13,13", "#CD1317");
    private static readonly Icon High = Make("M3,10.5 L8,5.5 L13,10.5", "#E9494A");
    private static readonly Icon Medium = Make("M3,6 H13 M3,10 H13", "#E97F33");
    private static readonly Icon Low = Make("M3,5.5 L8,10.5 L13,5.5", "#2684FF");
    private static readonly Icon Lowest = Make("M3,3 L8,8 L13,3 M3,7.5 L8,12.5 L13,7.5", "#0065FF");
    private static readonly Icon NoPriority = new(null, Brushes.Transparent);

    private static readonly Brush[] AvatarColors =
        new[] { "#0C66E4", "#1F845A", "#AE4787", "#6E5DC6", "#C25100", "#206A83", "#CA3521", "#7F5F01" }
            .Select(Freeze).ToArray();

    public static readonly Brush UnassignedColor = Freeze("#8590A2");

    // Status lozenges (dark theme), by Jira status category: "new" (To Do), "indeterminate" (In Progress), "done".
    // The StatusPill style in App.xaml uses these too, so the table and the issue panel always match.
    public static readonly Brush StatusNewBackground = Freeze("#38414A");
    public static readonly Brush StatusNewForeground = Freeze("#C7D1DB");
    public static readonly Brush StatusInProgressBackground = Freeze("#09326C");
    public static readonly Brush StatusInProgressForeground = Freeze("#85B8FF");
    public static readonly Brush StatusDoneBackground = Freeze("#164B35");
    public static readonly Brush StatusDoneForeground = Freeze("#7EE2B8");

    public static (Brush Background, Brush Foreground) ForStatus(string? category) => category switch
    {
        "indeterminate" => (StatusInProgressBackground, StatusInProgressForeground),
        "done" => (StatusDoneBackground, StatusDoneForeground),
        _ => (StatusNewBackground, StatusNewForeground),
    };

    public static Icon ForIssueType(string? type)
    {
        var t = type?.ToLowerInvariant() ?? "";
        if (t.Contains("bug")) return Bug;
        if (t.Contains("epic")) return Epic;
        if (t.Contains("sub")) return Subtask;   // "Sub-task", "Subtask" (before "task")
        if (t.Contains("story")) return Story;
        if (t.Contains("task")) return Task;
        return OtherType;
    }

    public static Icon ForPriority(string? priority) => priority?.ToLowerInvariant() switch
    {
        "highest" or "blocker" or "critical" => Highest,
        "high" or "major" => High,
        "medium" or "normal" => Medium,
        "low" or "minor" => Low,
        "lowest" or "trivial" => Lowest,
        null or "" => NoPriority,
        _ => Medium,
    };

    /// <summary>"Karthik Akala" → "KA", "admin" → "A". Empty for no name.</summary>
    public static string Initials(string? name)
    {
        var words = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "",
            1 => char.ToUpper(words[0][0], CultureInfo.CurrentCulture).ToString(),
            _ => string.Concat(char.ToUpper(words[0][0], CultureInfo.CurrentCulture), char.ToUpper(words[^1][0], CultureInfo.CurrentCulture)),
        };
    }

    /// <summary>The same name always gets the same colour (string.GetHashCode changes between runs, so it's computed here).</summary>
    public static Brush AvatarColor(string? name)
    {
        if (string.IsNullOrEmpty(name)) return UnassignedColor;
        var hash = 0;
        foreach (var c in name) hash = unchecked(hash * 31 + c);
        return AvatarColors[(hash & int.MaxValue) % AvatarColors.Length];
    }

    // ---- Icons as ready-drawn images, for the issue table ----
    // An Image of a frozen drawing costs almost nothing to lay out, while a Path (especially a stroked one)
    // recomputes its geometry bounds every time a recycled row shows a new issue. One image per icon, shared by all rows.

    private static readonly Dictionary<Icon, ImageSource> TypeImages = [];
    private static readonly Dictionary<Icon, ImageSource?> PriorityImages = [];

    /// <summary>The issue type icon (white shape on a coloured rounded square) as a 16×16 image.</summary>
    public static ImageSource TypeImage(string? type)
    {
        var icon = ForIssueType(type);
        lock (TypeImages)
        {
            if (!TypeImages.TryGetValue(icon, out var image))
            {
                var drawing = new DrawingGroup();
                drawing.Children.Add(new GeometryDrawing(icon.Color, null, new RectangleGeometry(new Rect(0, 0, 16, 16), 3, 3)));
                drawing.Children.Add(new GeometryDrawing(Brushes.White, null, icon.Shape));
                TypeImages[icon] = image = Freeze(new DrawingImage(drawing));
            }

            return image;
        }
    }

    /// <summary>The priority arrow as a 16×16 image; null when there's no priority.</summary>
    public static ImageSource? PriorityImage(string? priority)
    {
        var icon = ForPriority(priority);
        lock (PriorityImages)
        {
            if (!PriorityImages.TryGetValue(icon, out var image))
            {
                image = icon.Shape is null ? null : Freeze(new DrawingImage(new DrawingGroup
                {
                    Children =
                    {
                        // An invisible 16×16 box keeps every arrow in the same place, whatever its own size.
                        new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))),
                        new GeometryDrawing(null, new Pen(icon.Color, 1.8)
                        {
                            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
                        }, icon.Shape),
                    },
                }));
                PriorityImages[icon] = image;
            }

            return image;
        }
    }

    private static ImageSource Freeze(DrawingImage image)
    {
        image.Freeze();
        return image;
    }

    private static Icon Make(string path, string color)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return new Icon(geometry, Freeze(color));
    }

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();   // frozen brushes can be shared between rows and threads
        return brush;
    }
}

/// <summary>Issue type name → icon. ConverterParameter "shape" gives the geometry, anything else the background colour.</summary>
public sealed class IssueTypeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var icon = JiraVisuals.ForIssueType(value as string);
        return parameter as string == "shape" ? icon.Shape : icon.Color;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Priority name → arrow icon. ConverterParameter "shape" gives the geometry, anything else the colour.</summary>
public sealed class PriorityIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var icon = JiraVisuals.ForPriority(value as string);
        return parameter as string == "shape" ? icon.Shape : icon.Color;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A person's (or project's) name → initials for an avatar.</summary>
public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => JiraVisuals.Initials(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A person's (or project's) name → a stable avatar colour; grey when there's no name.</summary>
public sealed class AvatarColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => JiraVisuals.AvatarColor(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Upper-cases text, for Jira's status lozenges ("IN PROGRESS").</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
