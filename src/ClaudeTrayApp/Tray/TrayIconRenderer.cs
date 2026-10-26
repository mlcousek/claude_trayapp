using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeTrayApp.Core.Domain;

namespace ClaudeTrayApp.Tray;

/// <summary>
/// Draws the tray icon: a ring-arc of the primary window's utilisation around a compact numeral.
/// Rendered at the exact pixel size the taskbar wants (16 px at 100 % DPI) so nothing is resampled.
/// </summary>
public static class TrayIconRenderer
{
    /// <summary>Pixel sizes the taskbar uses at 100, 125, 150 and 200 % DPI.</summary>
    public static IReadOnlyList<int> StandardSizes { get; } = [16, 20, 24, 32];

    private static readonly Typeface NumeralTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <param name="badge">
    /// The letter that tells a second account's icon apart (instances started with <c>--claude-dir</c>): drawn in the
    /// bottom-right corner in the numeral's colour, never a status colour, with a transparent gap cut around its outline
    /// so the ring and the digits cannot run into it. Null or empty draws the icon exactly as a single account has it.
    /// </param>
    public static BitmapSource Render(TrayIconState state, int size, TrayPalette palette, string? badge = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);

        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);

        using (var context = visual.RenderOpen())
        {
            double extent = size;
            var center = new Point(extent / 2, extent / 2);
            var thickness = Math.Max(1.5, extent / 8);
            var radius = (extent / 2) - (thickness / 2) - 0.5;
            var badgeGeometry = string.IsNullOrEmpty(badge) ? null : BadgeGeometry(badge, extent);

            if (badgeGeometry is not null)
            {
                var gap = badgeGeometry.GetWidenedPathGeometry(Pen(Colors.Black, 2));
                var cut = new CombinedGeometry(GeometryCombineMode.Union, badgeGeometry, gap);
                context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, extent, extent)), cut));
            }

            context.DrawEllipse(null, Pen(palette.Track, thickness), center, radius, radius);

            if (state.Percent is { } percent && percent > 0)
            {
                var color = StatusColor(state.Status, palette);
                if (state.IsStale)
                {
                    color = Color.FromArgb(0x99, color.R, color.G, color.B);
                }

                var pen = Pen(color, thickness);
                var sweep = Math.Clamp(percent, 0, 100) / 100 * 360;
                if (sweep >= 359.5)
                {
                    context.DrawEllipse(null, pen, center, radius, radius);
                }
                else
                {
                    context.DrawGeometry(null, pen, ArcGeometry.Build(center, radius, -90, sweep));
                }
            }

            // The gap is cut from the ring only: the numeral is never moved, resized or clipped for the badge.
            if (badgeGeometry is not null)
            {
                context.Pop();
            }

            DrawNumeral(context, state.Text, extent, center, palette.Text);

            if (badgeGeometry is not null)
            {
                var brush = new SolidColorBrush(palette.Text);
                brush.Freeze();
                context.DrawGeometry(brush, null, badgeGeometry);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The badge letter's outline, its ink flush with the bottom-right corner, below the numeral's ink.</summary>
    internal static Geometry BadgeGeometry(string badge, double extent)
    {
        var formatted = new FormattedText(
            badge,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            NumeralTypeface,
            extent * BadgeFontScale,
            Brushes.Black,
            pixelsPerDip: 1.0);
        var geometry = formatted.BuildGeometry(new Point(0, 0));
        var ink = geometry.Bounds;
        geometry.Transform = new TranslateTransform(Math.Round(extent - ink.Right), Math.Round(extent - ink.Bottom));
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Font size of the badge as a share of the icon size: a capital about 4 px tall at 16 px.</summary>
    internal const double BadgeFontScale = 0.36;

    private static void DrawNumeral(DrawingContext context, string text, double extent, Point center, Color color)
    {
        var fontSize = text.Length >= 2 ? extent * 0.58 : extent * 0.72;
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            NumeralTypeface,
            fontSize,
            brush,
            pixelsPerDip: 1.0)
        {
            TextAlignment = TextAlignment.Center,
        };

        // Centre the ink, not the line box: digits have no descenders, so the line box sits too high.
        var inkBottom = formatted.Height + formatted.OverhangAfter;
        var inkTop = inkBottom - formatted.Extent;
        var originY = center.Y - ((inkTop + inkBottom) / 2);
        context.DrawText(formatted, new Point(center.X, originY));
    }

    private static Color StatusColor(UsageWindowStatus? status, TrayPalette palette) => status switch
    {
        UsageWindowStatus.Warning => palette.Warn,
        UsageWindowStatus.Critical or UsageWindowStatus.Exhausted => palette.Danger,
        _ => palette.Ok,
    };

    private static Pen Pen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }

}
