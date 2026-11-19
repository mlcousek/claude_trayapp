using System.Windows;
using System.Windows.Interop;
using ClaudeTrayApp.Interop;
using ClaudeTrayApp.Theming;

namespace ClaudeTrayApp.Views;

/// <summary>The About box: an ordinary top-level window, so closing the tray menu cannot dismiss it.</summary>
public partial class AboutWindow : Window
{
    private readonly ThemeManager _theme;

    public AboutWindow(string text, ThemeManager theme)
    {
        _theme = theme;
        InitializeComponent();
        AboutText.Text = text;
        CloseButton.Click += (_, _) => Close();
        _theme.ThemeChanged += OnThemeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkMode();
    }

    protected override void OnClosed(EventArgs e)
    {
        _theme.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    private void ApplyDarkMode()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        var dark = _theme.AppsTheme == AppTheme.Dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyDarkMode();
}
