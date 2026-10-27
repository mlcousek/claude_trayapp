using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Cache;
using ClaudeTrayApp.Core.Polling;
using ClaudeTrayApp.Core.Pricing;
using ClaudeTrayApp.Core.Providers;
using ClaudeTrayApp.Core.Settings;
using ClaudeTrayApp.Core.Storage;
using ClaudeTrayApp.Core.Updates;
using ClaudeTrayApp.Startup;
using ClaudeTrayApp.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace ClaudeTrayApp.Tests;

/// <summary>
/// The settings window of an account started with --claude-dir: theme and update check go to the shared root file,
/// everything else to the account's own file. Real stores over temp files; the rest of the window's collaborators
/// are the cheapest real or substitute objects that let it be constructed.
/// </summary>
public sealed class SettingsViewModelAccountTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));

    public SettingsViewModelAccountTests() => Directory.CreateDirectory(_directory);

    private string GlobalFile => Path.Combine(_directory, "settings.json");

    private string AccountFile => Path.Combine(_directory, "accounts", "work", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Shared_settings_go_to_the_root_file_and_the_rest_to_the_account_file()
    {
        StaThread.Run(() =>
        {
            using var global = Store(GlobalFile, excludeGlobals: false);
            using var account = Store(AccountFile, excludeGlobals: true);
            var viewModel = Create(account, global);
            try
            {
                viewModel.HasSeparateGlobalSettings.ShouldBeTrue();

                viewModel.SelectedTheme = SettingsViewModel.ThemeChoices.First(c => c.Value == ThemeSetting.Dark);
                viewModel.CheckForUpdates = false;
                viewModel.MaskEmail = false;

                global.Current.Theme.ShouldBe(ThemeSetting.Dark);
                global.Current.CheckForUpdates.ShouldBeFalse();
                global.Current.MaskEmail.ShouldBeTrue();
                account.Current.MaskEmail.ShouldBeFalse();
                File.ReadAllText(AccountFile).ShouldNotContain("\"theme\"");
            }
            finally
            {
                viewModel.Dispose();
            }

            return true;
        });
    }

    [Fact]
    public void Shared_settings_are_shown_from_the_root_file()
    {
        StaThread.Run(() =>
        {
            using var global = Store(GlobalFile, excludeGlobals: false);
            global.Update(s => s with { Theme = ThemeSetting.Light, CheckForUpdates = false });
            using var account = Store(AccountFile, excludeGlobals: true);
            var viewModel = Create(account, global);
            try
            {
                viewModel.SelectedTheme!.Value.ShouldBe(ThemeSetting.Light);
                viewModel.CheckForUpdates.ShouldBeFalse();
                viewModel.GlobalSettingsNote.ShouldContain(GlobalFile);
            }
            finally
            {
                viewModel.Dispose();
            }

            return true;
        });
    }

    [Fact]
    public void The_default_account_keeps_one_file_for_everything()
    {
        StaThread.Run(() =>
        {
            using var store = Store(GlobalFile, excludeGlobals: false);
            var viewModel = Create(store, global: null);
            try
            {
                viewModel.HasSeparateGlobalSettings.ShouldBeFalse();
                viewModel.WindowTitle.ShouldBe("Claude Usage Tray settings");

                viewModel.SelectedTheme = SettingsViewModel.ThemeChoices.First(c => c.Value == ThemeSetting.Dark);
                viewModel.MaskEmail = false;

                store.Current.Theme.ShouldBe(ThemeSetting.Dark);
                store.Current.MaskEmail.ShouldBeFalse();
            }
            finally
            {
                viewModel.Dispose();
            }

            return true;
        });
    }

    private static SettingsStore Store(string path, bool excludeGlobals)
    {
        var store = new SettingsStore(path, NullLogger<SettingsStore>.Instance, excludeGlobals: excludeGlobals);
        store.Load();
        return store;
    }

    private SettingsViewModel Create(SettingsStore store, SettingsStore? global)
    {
        var pricingFile = Path.Combine(_directory, "pricing.json");
        File.WriteAllText(pricingFile, """{"effectiveDate":"2026-06-24","currency":"USD","models":[{"id":"claude-a","input":1,"output":2}]}""");
        var projects = Path.Combine(_directory, "projects");
        var scanner = new JsonlScanner(projects, Substitute.For<IAnalyticsStore>(), TimeProvider.System, NullLogger<JsonlScanner>.Instance, TimeSpan.FromDays(90));
        var poller = new UsagePoller(Substitute.For<IUsageProvider>(), Substitute.For<ISnapshotCache>(), new PollingOptions(), TimeProvider.System, NullLogger<UsagePoller>.Instance);

        return new SettingsViewModel(
            store,
            Substitute.For<IAutostartEntry>(),
            new PricingProvider(pricingFile, NullLogger<PricingProvider>.Instance),
            poller,
            Substitute.For<IHistoryStore>(),
            new LocalAnalyticsProvider(scanner, projects, NullLogger<LocalAnalyticsProvider>.Instance),
            new UpdateNotifier(),
            new UpdateChecker(new HttpClient(), "0.0.0", NullLogger<UpdateChecker>.Instance),
            Dispatcher.CurrentDispatcher,
            NullLogger<SettingsViewModel>.Instance,
            global);
    }
}
