using System.IO;
using System.Security;
using System.Windows;
using ClaudeTrayApp.Tray;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClaudeTrayApp.Theming;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Follows the Windows light/dark setting. Swaps the colour palette dictionary in the application resources
/// (apps theme) and exposes the taskbar theme separately, because the tray icon sits on the taskbar, not in the app.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";
    private const string SystemUsesLightTheme = "SystemUsesLightTheme";
    private static readonly Uri DarkPaletteUri = new("pack://application:,,,/Themes/Palette.Dark.xaml");
    private static readonly Uri LightPaletteUri = new("pack://application:,,,/Themes/Palette.Light.xaml");

    private readonly Application _application;
    private readonly ILogger<ThemeManager> _logger;
    private ResourceDictionary? _darkPalette;
    private ResourceDictionary? _lightPalette;
    private ResourceDictionary? _activePalette;

    public ThemeManager(Application application, ILogger<ThemeManager> logger)
    {
        _application = application;
        _logger = logger;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler? ThemeChanged;

    /// <summary>Theme used for the app's own windows.</summary>
    public AppTheme AppsTheme { get; private set; } = AppTheme.Dark;

    /// <summary>Theme of the taskbar, which decides the tray icon's text colour.</summary>
    public AppTheme TaskbarTheme { get; private set; } = AppTheme.Dark;

    /// <summary>Forces a theme regardless of the Windows setting (settings window, milestone 7).</summary>
    public AppTheme? Override { get; set; }

    /// <summary>Reads the Windows setting and merges the matching palette. Safe to call repeatedly.</summary>
    public void Apply()
    {
        var apps = Override ?? ReadTheme(AppsUseLightTheme);
        var taskbar = ReadTheme(SystemUsesLightTheme);
        var changed = _activePalette is null || apps != AppsTheme || taskbar != TaskbarTheme;

        AppsTheme = apps;
        TaskbarTheme = taskbar;

        var palette = Palette(apps);
        if (!ReferenceEquals(palette, _activePalette))
        {
            var merged = _application.Resources.MergedDictionaries;
            if (_activePalette is not null)
            {
                merged.Remove(_activePalette);
            }

            merged.Insert(0, palette);
            _activePalette = palette;
        }

        if (changed)
        {
            _logger.LogDebug("Theme applied: apps {Apps}, taskbar {Taskbar}", apps, taskbar);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Palette for the tray icon: text colour from the taskbar theme, status colours from the same palette.</summary>
    public TrayPalette GetTrayPalette() => GetTrayPalette(TaskbarTheme);

    public TrayPalette GetTrayPalette(AppTheme theme) => TrayPalette.FromDictionary(Palette(theme));

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>Windows stores 1 for light and 0 for dark. A missing value means Windows defaults, which is light.</summary>
    internal static AppTheme ReadTheme(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(valueName) is int value && value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return AppTheme.Light;
        }
    }

    private ResourceDictionary Palette(AppTheme theme) => theme == AppTheme.Light
        ? _lightPalette ??= new ResourceDictionary { Source = LightPaletteUri }
        : _darkPalette ??= new ResourceDictionary { Source = DarkPaletteUri };

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color or UserPreferenceCategory.Desktop)
        {
            _application.Dispatcher.BeginInvoke(Apply);
        }
    }
}
