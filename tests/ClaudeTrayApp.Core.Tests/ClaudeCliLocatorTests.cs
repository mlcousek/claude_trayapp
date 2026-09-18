using ClaudeTrayApp.Core.ClaudeCode;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class ClaudeCliLocatorTests
{
    private static readonly string Desktop = Path.Combine("C:", "Users", "me", "AppData", "Roaming", "Claude", "claude-code");
    private static readonly string Tools = Path.Combine("C:", "tools");
    private static readonly string Npm = Path.Combine("C:", "npm");

    private static ClaudeCliLocator Create(
        string? path,
        IEnumerable<string> existingFiles,
        IEnumerable<string>? desktopVersions = null,
        Action<string>? onExists = null)
    {
        var files = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        var versions = (desktopVersions ?? []).Select(v => Path.Combine(Desktop, v)).ToList();
        return new ClaudeCliLocator(
            path,
            Desktop,
            candidate =>
            {
                onExists?.Invoke(candidate);
                return files.Contains(candidate);
            },
            directory => directory == Desktop ? versions : [],
            new ListLogger<ClaudeCliLocator>());
    }

    [Fact]
    public void Prefers_the_native_executable_on_path()
    {
        var locator = Create(string.Join(Path.PathSeparator, Npm, Tools), [Path.Combine(Tools, "claude.exe")], ["2.1.275"]);

        var location = locator.Locate().ShouldNotBeNull();

        location.Path.ShouldBe(Path.Combine(Tools, "claude.exe"));
        location.Kind.ShouldBe(ClaudeCliKind.Executable);
        location.Source.ShouldBe(ClaudeCliLocator.PathSource);
        var (fileName, arguments) = location.Command(["--version"]);
        fileName.ShouldBe(Path.Combine(Tools, "claude.exe"));
        arguments.ShouldBe(["--version"]);
    }

    [Fact]
    public void An_npm_shim_runs_through_cmd()
    {
        var shim = Path.Combine(Npm, "claude.cmd");
        var locator = Create($"\"{Npm}\"{Path.PathSeparator}{Tools}", [shim]);

        var location = locator.Locate().ShouldNotBeNull();

        location.Kind.ShouldBe(ClaudeCliKind.Shim);
        var (fileName, arguments) = location.Command(["-p", "--verbose"]);
        fileName.ShouldBe("cmd.exe");
        arguments.ShouldBe(["/d", "/c", shim, "-p", "--verbose"]);
    }

    [Fact]
    public void Falls_back_to_the_newest_build_bundled_with_claude_desktop()
    {
        var newest = Path.Combine(Desktop, "2.1.275", "claude.exe");
        var locator = Create(Tools, [Path.Combine(Desktop, "2.1.271", "claude.exe"), newest], ["2.1.271", "2.1.275", "notes", "2.1.9"]);

        var location = locator.Locate().ShouldNotBeNull();

        location.Path.ShouldBe(newest);
        location.Kind.ShouldBe(ClaudeCliKind.Executable);
        location.Source.ShouldBe(ClaudeCliLocator.DesktopSource);
    }

    [Fact]
    public void Skips_a_bundled_folder_without_the_executable()
    {
        var older = Path.Combine(Desktop, "2.1.271", "claude.exe");
        var locator = Create(null, [older], ["2.1.271", "2.1.275"]);

        locator.Locate().ShouldNotBeNull().Path.ShouldBe(older);
    }

    [Fact]
    public void Reports_nothing_when_neither_path_nor_desktop_has_a_cli()
    {
        var locator = Create(Tools, [], ["2.1.275"]);

        locator.Locate().ShouldBeNull();
    }

    [Fact]
    public void Looks_only_once_per_process()
    {
        var probes = 0;
        var locator = Create(Tools, [], onExists: _ => probes++);

        locator.Locate();
        locator.Locate();
        var afterFirst = probes;
        locator.Locate();

        afterFirst.ShouldBeGreaterThan(0);
        probes.ShouldBe(afterFirst);
    }

    [Theory]
    [InlineData("2.1.275", "2.1.275")]
    [InlineData("10.0.1", "10.0.1")]
    [InlineData("2.1.275-beta", null)]
    [InlineData("latest", null)]
    public void Parses_bundled_version_folders(string folder, string? expected) =>
        ClaudeCliLocator.ParseBundledVersion(Path.Combine(Desktop, folder))?.ToString().ShouldBe(expected);
}
