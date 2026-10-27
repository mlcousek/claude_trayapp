using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Tray;
using Shouldly;

namespace ClaudeTrayApp.Tests;

public class TrayIconRendererTests
{
    private static readonly TrayPalette Palette = new(
        Colors.White,
        Color.FromRgb(0x7B, 0xA0, 0x5B),
        Color.FromRgb(0xD4, 0xA2, 0x4C),
        Color.FromRgb(0xC1, 0x55, 0x4A));

    private static BitmapSource Render(TrayIconState state, int size = 32) =>
        StaThread.Run(() => TrayIconRenderer.Render(state, size, Palette));

    /// <summary>BGRA of one pixel (premultiplied alpha).</summary>
    private static (byte B, byte G, byte R, byte A) Pixel(BitmapSource bitmap, int x, int y)
    {
        var buffer = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Renders_at_the_exact_pixel_size(int size)
    {
        var bitmap = Render(new TrayIconState(45, UsageWindowStatus.Ok, false), size);

        bitmap.PixelWidth.ShouldBe(size);
        bitmap.PixelHeight.ShouldBe(size);
        bitmap.IsFrozen.ShouldBeTrue();
    }

    [Fact]
    public void The_arc_uses_the_status_colour_and_starts_at_twelve_o_clock()
    {
        // 45 %: the arc covers the top and the right side but not the left.
        var bitmap = Render(new TrayIconState(45, UsageWindowStatus.Ok, false));

        var top = Pixel(bitmap, 16, 2);
        var right = Pixel(bitmap, 29, 16);
        var left = Pixel(bitmap, 2, 16);

        top.A.ShouldBeGreaterThan((byte)200);
        top.G.ShouldBeGreaterThan(top.R);
        right.G.ShouldBeGreaterThan(right.R);
        left.A.ShouldBeLessThan((byte)128);
    }

    [Fact]
    public void A_second_accounts_healthy_arc_is_yellow_and_warnings_keep_their_colours()
    {
        var palette = Palette with { Account2 = Color.FromRgb(0xE8, 0xD4, 0x4D) };

        var healthy = StaThread.Run(() => TrayIconRenderer.Render(new TrayIconState(45, UsageWindowStatus.Ok, false), 32, palette.ForSecondAccount()));
        var top = Pixel(healthy, 16, 2);
        top.R.ShouldBeGreaterThan((byte)200);
        top.G.ShouldBeGreaterThan((byte)180);
        top.B.ShouldBeLessThan((byte)120);

        palette.ForSecondAccount().Warn.ShouldBe(palette.Warn);
        palette.ForSecondAccount().Danger.ShouldBe(palette.Danger);
        Palette.ForSecondAccount().ShouldBe(Palette);
    }

    [Fact]
    public void Critical_windows_draw_a_red_arc()
    {
        var top = Pixel(Render(new TrayIconState(93, UsageWindowStatus.Critical, false)), 16, 2);

        top.R.ShouldBeGreaterThan(top.G);
        top.R.ShouldBeGreaterThan(top.B);
    }

    [Fact]
    public void Stale_data_draws_a_translucent_arc()
    {
        var fresh = Pixel(Render(new TrayIconState(93, UsageWindowStatus.Critical, false)), 16, 2);
        var stale = Pixel(Render(new TrayIconState(93, UsageWindowStatus.Critical, true)), 16, 2);

        stale.A.ShouldBeLessThan(fresh.A);
        stale.A.ShouldBeGreaterThan((byte)100);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void A_badge_sits_in_the_bottom_right_corner_in_the_numeral_colour(int size)
    {
        var state = new TrayIconState(45, UsageWindowStatus.Ok, false);
        var bounds = StaThread.Run(() => TrayIconRenderer.BadgeGeometry("W", size).Bounds);

        bounds.Right.ShouldBe(size, 0.5);
        bounds.Bottom.ShouldBe(size, 0.5);
        bounds.Left.ShouldBeGreaterThan(size / 2.0);
        bounds.Top.ShouldBeGreaterThan(size / 2.0);

        // Inside the letter's box the badged icon is mostly the numeral's white; the plain one has the ring there.
        var box = new Int32Rect((int)bounds.Left, (int)bounds.Top, size - (int)bounds.Left, size - (int)bounds.Top);
        var plain = WhitePixels(Render(state, size), box);
        var badged = WhitePixels(StaThread.Run(() => TrayIconRenderer.Render(state, size, Palette, "W")), box);

        badged.ShouldBeGreaterThan(plain + (box.Width * box.Height / 5));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void A_badge_never_moves_or_touches_the_numeral(int size)
    {
        var state = new TrayIconState(86, UsageWindowStatus.Warning, false);
        var bounds = StaThread.Run(() => TrayIconRenderer.BadgeGeometry("W", size).Bounds);
        var plain = Render(state, size);
        var badged = StaThread.Run(() => TrayIconRenderer.Render(state, size, Palette, "W"));

        // Everything outside the letter and its one-pixel gap is identical to the single-account icon.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x >= bounds.Left - 2 && y >= bounds.Top - 2)
                {
                    continue;
                }

                Pixel(badged, x, y).ShouldBe(Pixel(plain, x, y), $"pixel ({x},{y}) at {size} px");
            }
        }
    }

    /// <summary>Pixels in <paramref name="box"/> that are nearly opaque white, the palette's text colour.</summary>
    private static int WhitePixels(BitmapSource bitmap, Int32Rect box)
    {
        var count = 0;
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            for (var x = box.X; x < box.X + box.Width; x++)
            {
                var (b, g, r, a) = Pixel(bitmap, x, y);
                if (a > 150 && r > 150 && g > 150 && b > 150)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void An_icon_without_a_badge_is_unchanged_pixel_for_pixel()
    {
        var state = new TrayIconState(86, UsageWindowStatus.Warning, false);

        var before = Bytes(Render(state, 16));
        var none = Bytes(StaThread.Run(() => TrayIconRenderer.Render(state, 16, Palette, badge: null)));
        var empty = Bytes(StaThread.Run(() => TrayIconRenderer.Render(state, 16, Palette, badge: string.Empty)));
        var badged = Bytes(StaThread.Run(() => TrayIconRenderer.Render(state, 16, Palette, badge: "W")));

        none.ShouldBe(before);
        empty.ShouldBe(before);
        badged.ShouldNotBe(before);
    }

    private static byte[] Bytes(BitmapSource bitmap)
    {
        var buffer = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(buffer, bitmap.PixelWidth * 4, 0);
        return buffer;
    }

    [Fact]
    public void Unknown_state_has_only_the_faint_track()
    {
        var top = Pixel(Render(TrayIconState.Unknown), 16, 2);

        top.A.ShouldBeLessThan((byte)128);
    }
}
