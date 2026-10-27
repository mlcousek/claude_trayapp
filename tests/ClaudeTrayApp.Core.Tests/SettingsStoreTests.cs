using ClaudeTrayApp.Core.Settings;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    /// <summary>How settings.json was written before accounts existed; the default account's file must not change.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions PreviousFormat = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void Load_writes_the_defaults_when_there_is_no_file()
    {
        using var store = Create();

        var settings = store.Load();

        settings.ShouldBe(AppSettings.Default);
        File.Exists(SettingsPath).ShouldBeTrue();
        var json = File.ReadAllText(SettingsPath);
        json.ShouldContain("\"pollIntervalSeconds\": 300");
        json.ShouldContain("\"theme\": \"system\"");
        json.ShouldContain("\"trayWindow\": \"auto\"");
        json.ShouldContain("\"thresholds\": [");
        json.ShouldContain("\"maskEmail\": true");
        json.ShouldContain("\"showLocalAnalytics\": false");
        json.ShouldContain("\"showExtraUsage\": false");
        json.ShouldContain("\"showInactiveWindows\": false");
        store.LastError.ShouldBeNull();
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var settings = new AppSettings
        {
            PollIntervalSeconds = 600,
            TrayWindow = "seven_day",
            ChartRangeHours = 168,
            HistoryRetentionDays = 30,
            Notifications = new NotificationSettings { Enabled = true, Thresholds = [50, 90] },
            ShowLocalAnalytics = true,
            ShowExtraUsage = true,
            ShowInactiveWindows = true,
            MaskEmail = false,
            Theme = ThemeSetting.Dark,
            PricingFilePath = @"C:\prices\pricing.json",
        };

        using (var writer = Create())
        {
            writer.Save(settings);
        }

        using var reader = Create();
        reader.Load().ShouldBe(settings);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_load()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            {
              "pollIntervalSeconds": 10,
              "historyRetentionDays": 0,
              "chartRangeHours": 5,
              "trayWindow": "  ",
              "notifications": { "enabled": true, "thresholds": [150, 95, 80, 80, -1] },
              "pricingFilePath": "  ",
              "somethingFromTheFuture": true
            }
            """);
        using var store = Create();

        var settings = store.Load();

        settings.PollIntervalSeconds.ShouldBe(AppSettings.MinimumPollIntervalSeconds);
        settings.HistoryRetentionDays.ShouldBe(1);
        settings.ChartRangeHours.ShouldBe(24);
        settings.TrayWindow.ShouldBe("auto");
        settings.Notifications.Enabled.ShouldBeTrue();
        settings.Notifications.Thresholds.ShouldBe([80, 95]);
        settings.PricingFilePath.ShouldBeNull();
        store.LastError.ShouldBeNull();
    }

    [Fact]
    public void A_broken_file_is_left_alone_and_reported()
    {
        Directory.CreateDirectory(_directory);
        const string Broken = "{ \"pollIntervalSeconds\": 600, ";
        File.WriteAllText(SettingsPath, Broken);
        using var store = Create();

        var settings = store.Load();

        settings.ShouldBe(AppSettings.Default);
        store.LastError.ShouldNotBeNull().ShouldContain("not valid JSON");
        File.ReadAllText(SettingsPath).ShouldBe(Broken);
    }

    [Fact]
    public void Reload_ignores_its_own_write_and_picks_up_external_edits()
    {
        using var store = Create();
        var changes = new List<AppSettings>();
        store.Changed += (_, s) => changes.Add(s);
        store.Load();

        store.Reload().ShouldBeFalse();
        changes.ShouldBeEmpty();

        File.WriteAllText(SettingsPath, "{ \"pollIntervalSeconds\": 900, \"theme\": \"light\" }");
        store.Reload().ShouldBeTrue();

        store.Current.PollIntervalSeconds.ShouldBe(900);
        store.Current.Theme.ShouldBe(ThemeSetting.Light);
        changes.Count.ShouldBe(1);
        store.Reload().ShouldBeFalse();
    }

    [Fact]
    public void Update_persists_and_raises_once()
    {
        using var store = Create();
        store.Load();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Update(s => s with { MaskEmail = false });
        store.Update(s => s with { MaskEmail = false });

        raised.ShouldBe(1);
        File.ReadAllText(SettingsPath).ShouldContain("\"maskEmail\": false");
    }

    [Fact]
    public void A_change_switched_back_by_another_process_is_followed()
    {
        using var store = Create();
        store.Load();
        var ownText = File.ReadAllText(SettingsPath);

        File.WriteAllText(SettingsPath, ownText.Replace("\"theme\": \"system\"", "\"theme\": \"light\"", StringComparison.Ordinal));
        store.Reload().ShouldBeTrue();
        store.Current.Theme.ShouldBe(ThemeSetting.Light);

        // The other process restores the very text this store wrote; it is still a change to follow.
        File.WriteAllText(SettingsPath, ownText);
        store.Reload().ShouldBeTrue();
        store.Current.Theme.ShouldBe(ThemeSetting.System);
    }

    [Fact]
    public void Update_keeps_a_change_another_process_made_moments_ago()
    {
        using var store = Create();
        store.Load();

        // Another account's instance wrote the shared file; this store's watcher has not caught up yet.
        File.WriteAllText(SettingsPath, "{ \"pollIntervalSeconds\": 900 }");
        store.Update(s => s with { Theme = ThemeSetting.Dark });

        using var reader = Create();
        var settings = reader.Load();
        settings.PollIntervalSeconds.ShouldBe(900);
        settings.Theme.ShouldBe(ThemeSetting.Dark);
    }

    [Fact]
    public async Task Two_stores_updating_one_file_at_once_keep_each_others_edits()
    {
        // Two account processes sharing the root settings.json: one keeps changing the theme, the other the poll interval.
        using var first = Create();
        using var second = Create();
        first.Load();
        second.Load();
        var themes = new[] { ThemeSetting.Light, ThemeSetting.Dark };
        var intervals = new[] { 600, 900 };

        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 40; i++) { first.Update(s => s with { Theme = themes[i % 2] }); } }, TestContext.Current.CancellationToken),
            Task.Run(() => { for (var i = 0; i < 40; i++) { second.Update(s => s with { PollIntervalSeconds = intervals[i % 2] }); } }, TestContext.Current.CancellationToken));

        using var reader = Create();
        var settings = reader.Load();
        settings.Theme.ShouldBe(ThemeSetting.Dark);
        settings.PollIntervalSeconds.ShouldBe(900);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void Each_settings_file_has_one_lock_name_whatever_its_spelling()
    {
        SettingsStore.MutexNameFor(@"C:\Users\Me\AppData\Roaming\ClaudeTrayApp\settings.json")
            .ShouldBe(SettingsStore.MutexNameFor(@"c:\users\me\appdata\roaming\claudetrayapp\SETTINGS.JSON"));
        SettingsStore.MutexNameFor(@"C:\a\settings.json").ShouldNotBe(SettingsStore.MutexNameFor(@"C:\b\settings.json"));
        SettingsStore.MutexNameFor(@"C:\a\settings.json").ShouldStartWith(@"Local\ClaudeUsageTray.Settings.");
    }

    [Fact]
    public void A_store_that_excludes_globals_neither_writes_nor_honours_them()
    {
        using var store = Create(excludeGlobals: true);

        store.Load();
        store.Update(s => s with { Theme = ThemeSetting.Dark, CheckForUpdates = false, PollIntervalSeconds = 600 });

        var json = File.ReadAllText(SettingsPath);
        json.ShouldNotContain("\"theme\"");
        json.ShouldNotContain("\"checkForUpdates\"");
        json.ShouldContain("\"pollIntervalSeconds\": 600");
        store.Current.Theme.ShouldBe(AppSettings.Default.Theme);
        store.Current.CheckForUpdates.ShouldBe(AppSettings.Default.CheckForUpdates);
    }

    [Fact]
    public void Hand_edited_globals_in_an_account_file_are_ignored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ \"theme\": \"dark\", \"checkForUpdates\": false, \"maskEmail\": false }");
        using var store = Create(excludeGlobals: true);

        var settings = store.Load();

        settings.Theme.ShouldBe(AppSettings.Default.Theme);
        settings.CheckForUpdates.ShouldBe(AppSettings.Default.CheckForUpdates);
        settings.MaskEmail.ShouldBeFalse();
    }

    [Fact]
    public void A_store_that_keeps_globals_writes_the_same_bytes_as_before()
    {
        using var store = Create();

        store.Load();

        File.ReadAllText(SettingsPath).ShouldBe(System.Text.Json.JsonSerializer.Serialize(AppSettings.Default, PreviousFormat) + Environment.NewLine);
    }

    [Fact]
    public async Task External_edits_are_reloaded_by_the_watcher()
    {
        using var store = Create(TimeSpan.FromMilliseconds(50));
        store.Load();
        var changed = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, s) => changed.TrySetResult(s);
        store.StartWatching();

        File.WriteAllText(SettingsPath, "{ \"historyRetentionDays\": 45 }");

        var finished = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        finished.ShouldBe(changed.Task, "the watcher did not reload the file");
        (await changed.Task).HistoryRetentionDays.ShouldBe(45);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A watcher may still hold the folder for a moment; the temp folder is cleaned by the OS.
        }
    }

    private SettingsStore Create(TimeSpan? debounce = null, bool excludeGlobals = false) =>
        new(SettingsPath, new ListLogger<SettingsStore>(), debounce, excludeGlobals);
}
