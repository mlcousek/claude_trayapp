using System.Windows;
using ClaudeTrayApp.Theming;
using ClaudeTrayApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeTrayApp.Startup;

/// <summary>Opens the About window at most once at a time; a second request brings the open one forward.</summary>
public sealed class AboutWindowHost
{
    private readonly IServiceProvider _services;
    private AboutWindow? _current;

    public AboutWindowHost(IServiceProvider services)
    {
        _services = services;
    }

    public void Show(string text)
    {
        if (_current is null)
        {
            var window = new AboutWindow(text, _services.GetRequiredService<ThemeManager>());
            window.Closed += (_, _) => _current = null;
            _current = window;
            window.Show();
        }
        else if (_current.WindowState == WindowState.Minimized)
        {
            _current.WindowState = WindowState.Normal;
        }

        _current.Activate();
    }
}
