using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using ClaudeTrayApp.Interop;
using ClaudeTrayApp.Theming;
using ClaudeTrayApp.ViewModels;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClaudeTrayApp.Tray;

/// <summary>
/// Owns the taskbar icon: draws it from the view model, redraws on DPI or theme changes, wires the context menu.
/// Left-click is surfaced as an event for the flyout (milestone 4).
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly TrayIconViewModel _viewModel;
    private readonly ThemeManager _theme;
    private readonly ILogger<TrayIconController> _logger;
    private int _lastSize;

    public TrayIconController(TrayIconViewModel viewModel, ThemeManager theme, ILogger<TrayIconController> logger)
    {
        _viewModel = viewModel;
        _theme = theme;
        _logger = logger;

        _icon = new TaskbarIcon
        {
            ToolTipText = viewModel.Tooltip,
            ContextMenu = BuildMenu(viewModel),
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        AutomationProperties.SetName(_icon, "Claude Usage Tray");

        _icon.TrayLeftMouseUp += OnTrayLeftMouseUp;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.NotificationRequested += OnNotificationRequested;
        _theme.ThemeChanged += OnThemeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Redraw();
        _icon.ForceCreate();
        _logger.LogInformation("Tray icon created at {Size} px", _lastSize);
    }

    /// <summary>Left-click on the icon. The flyout subscribes here from milestone 4.</summary>
    public event EventHandler? LeftClick;

    /// <summary>Icon pixel size for the current system DPI: 16 at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %.</summary>
    internal static int IconPixelSize()
    {
        double dpi;
        try
        {
            dpi = NativeMethods.GetDpiForSystem();
        }
        catch (EntryPointNotFoundException)
        {
            dpi = 96;
        }

        return Math.Clamp((int)Math.Round(16 * dpi / 96), 16, 64);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _theme.ThemeChanged -= OnThemeChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.NotificationRequested -= OnNotificationRequested;
        _icon.TrayLeftMouseUp -= OnTrayLeftMouseUp;
        var icon = _icon.Icon;
        _icon.Dispose();
        icon?.Dispose();
    }

    /// <summary>Shows a Windows notification from the tray icon.</summary>
    public void ShowNotification(TrayNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        _icon.ShowNotification(notification.Title, notification.Message, notification.IsWarning ? NotificationIcon.Warning : NotificationIcon.Info);
    }

    private static ContextMenu BuildMenu(TrayIconViewModel viewModel)
    {
        var menu = new ContextMenu { DataContext = viewModel };
        AutomationProperties.SetName(menu, "Claude Usage Tray menu");
        menu.Items.Add(new MenuItem { Header = "_Refresh", Command = viewModel.RefreshCommand });
        menu.Items.Add(new MenuItem { Header = "_Settings…", Command = viewModel.SettingsCommand });
        menu.Items.Add(new MenuItem { Header = "Open _logs", Command = viewModel.OpenLogsCommand });

        var autostart = new MenuItem { Header = "Start with _Windows", IsCheckable = true };
        BindingOperations.SetBinding(autostart, MenuItem.IsCheckedProperty, new Binding(nameof(TrayIconViewModel.StartWithWindows)) { Mode = BindingMode.TwoWay });
        menu.Items.Add(autostart);
        menu.Opened += (_, _) => viewModel.RefreshAutostart();
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "_About", Command = viewModel.AboutCommand });
        menu.Items.Add(new MenuItem { Header = "_Quit", Command = viewModel.QuitCommand });
        return menu;
    }

    private void Redraw()
    {
        try
        {
            _lastSize = IconPixelSize();
            var bitmap = TrayIconRenderer.Render(_viewModel.IconState, _lastSize, _theme.GetTrayPalette());
            var previous = _icon.Icon;
            _icon.Icon = IconConverter.ToIcon(bitmap);
            previous?.Dispose();
        }
        catch (Exception ex)
        {
            // A failed redraw keeps the previous icon; it must never take the app down.
            _logger.LogError(ex, "Tray icon redraw failed");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrayIconViewModel.IconState))
        {
            Redraw();
        }
        else if (e.PropertyName == nameof(TrayIconViewModel.Tooltip))
        {
            _icon.ToolTipText = _viewModel.Tooltip;
        }
    }

    private void OnNotificationRequested(object? sender, TrayNotification notification) => ShowNotification(notification);

    private void OnThemeChanged(object? sender, EventArgs e) => Redraw();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _icon.Dispatcher.BeginInvoke(Redraw);

    private void OnTrayLeftMouseUp(object sender, System.Windows.RoutedEventArgs e) => LeftClick?.Invoke(this, EventArgs.Empty);
}
