using System.Windows;
using System.Windows.Media;

namespace ClaudeTrayApp.Tray;

/// <summary>The few colours the tray icon needs, resolved from a palette dictionary so nothing is hardcoded.</summary>
public sealed record TrayPalette(Color Text, Color Ok, Color Warn, Color Danger, Color? Account2 = null)
{
    /// <summary>The ring colour of a second account's icon: its own colour while healthy, the same warning and danger colours after that.</summary>
    public TrayPalette ForSecondAccount() => Account2 is { } color ? this with { Ok = color } : this;

    /// <summary>The unfilled part of the ring: the text colour at low alpha.</summary>
    public Color Track => Color.FromArgb(0x48, Text.R, Text.G, Text.B);

    public static TrayPalette FromDictionary(ResourceDictionary palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new TrayPalette(
            Read(palette, "TextPrimaryColor"),
            Read(palette, "OkColor"),
            Read(palette, "WarnColor"),
            Read(palette, "DangerColor"),
            palette["Account2Color"] as Color?);
    }

    private static Color Read(ResourceDictionary palette, string key) =>
        palette[key] is Color color ? color : throw new InvalidOperationException($"Theme token '{key}' is missing.");
}
