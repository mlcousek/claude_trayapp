using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeTrayApp.Interop;
using ClaudeTrayApp.Theming;
using ClaudeTrayApp.Tray;
using ClaudeTrayApp.ViewModels;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Views;

/// <summary>
/// The flyout: a tool window anchored to the taskbar edge next to the tray icon, with an acrylic backdrop when
/// Windows provides one and a solid surface otherwise. Hidden, never closed, so it reopens in a few milliseconds.
/// </summary>
public partial class FlyoutWindow : Window
{
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(250);
    private readonly FlyoutViewModel _viewModel;
    private readonly ThemeManager _theme;
    private readonly ILogger<FlyoutWindow> _logger;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private nint _handle;
    private DateTime _hiddenAt = DateTime.MinValue;

    public FlyoutWindow(FlyoutViewModel viewModel, ThemeManager theme, ILogger<FlyoutWindow> logger)
    {
        _viewModel = viewModel;
        _theme = theme;
        _logger = logger;

        InitializeComponent();
        DataContext = viewModel;

        _tick.Tick += (_, _) => _viewModel.Tick();
        Deactivated += (_, _) => HideFlyout();
        _theme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Creates the native window and runs one layout pass off-screen, so the first real open pays only for
    /// placement and paint instead of building the visual tree.
    /// </summary>
    public void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();
        Left = -32000;
        Top = -32000;
        ShowActivated = false;
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = true;
        _hiddenAt = DateTime.MinValue;
    }

    /// <summary>Opens or closes the flyout. A click on the tray icon that just dismissed it does not reopen it.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }

        if (DateTime.UtcNow - _hiddenAt < ReopenGuard)
        {
            return;
        }

        ShowFlyout();
    }

    public void ShowFlyout()
    {
        var stopwatch = Stopwatch.StartNew();
        _viewModel.Tick();

        NativeMethods.GetCursorPos(out var anchor);
        Show();
        UpdateLayout();
        Place(anchor);
        Activate();
        RefreshButton.Focus();
        FadeIn();
        _tick.Start();

        _logger.LogDebug("Flyout shown in {Elapsed} ms", stopwatch.ElapsedMilliseconds);
    }

    public void HideFlyout()
    {
        if (!IsVisible)
        {
            return;
        }

        _tick.Stop();
        Hide();
        _hiddenAt = DateTime.UtcNow;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        MakeToolWindow();
        ApplyBackdrop();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsVisible && NativeMethods.GetCursorPos(out var anchor))
        {
            Dispatcher.BeginInvoke(() => Place(anchor), DispatcherPriority.Loaded);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _theme.ThemeChanged -= OnThemeChanged;
        _tick.Stop();
        base.OnClosed(e);
    }

    /// <summary>Positions the window in physical pixels on the monitor under the anchor, hugging the taskbar edge.</summary>
    private void Place(POINT anchor)
    {
        if (_handle == 0)
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromPoint(anchor, NativeMethods.MonitorDefaultToNearest);
        var info = new MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var margin = (int)Math.Round(12 * scale);
        var (x, y, edge) = FlyoutPlacement.Compute(
            new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom),
            anchor.X,
            anchor.Y,
            width,
            height,
            margin);

        NativeMethods.SetWindowPos(_handle, 0, x, y, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        _logger.LogDebug("Flyout placed at ({X},{Y}) on the {Edge} edge, {Width}x{Height} px", x, y, edge, width, height);
    }

    /// <summary>WS_EX_TOOLWINDOW keeps the flyout out of Alt-Tab and off the taskbar.</summary>
    private void MakeToolWindow()
    {
        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, style | NativeMethods.WsExToolWindow);
    }

    private void ApplyBackdrop()
    {
        ApplyDarkMode();

        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var backdrop = NativeMethods.DwmsbtTransientWindow;
        var supported = NativeMethods.DwmExtendFrameIntoClientArea(_handle, ref margins) == 0
            && NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;

        if (supported)
        {
            Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(_handle) is { } source)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            RootBorder.SetResourceReference(BackgroundProperty, "SurfaceTranslucentBrush");
        }
        else
        {
            RootBorder.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        }

        _logger.LogDebug("Flyout backdrop: {Mode}", supported ? "acrylic" : "solid");
    }

    private void ApplyDarkMode()
    {
        if (_handle == 0)
        {
            return;
        }

        var dark = _theme.AppsTheme == AppTheme.Dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(_handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    /// <summary>A 120 ms fade on the content, skipped when Windows animations are turned off.</summary>
    private void FadeIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Body.Opacity = 1;
            return;
        }

        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
        Body.BeginAnimation(OpacityProperty, animation);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyDarkMode();
}
