using ClaudeTrayApp.Core.ClaudeCode;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class ClaudeCodeVersionDetectorTests
{
    [Theory]
    [InlineData("2.1.224 (Claude Code)", "2.1.224")]
    [InlineData("2.1.260", "2.1.260")]
    [InlineData("  v10.0.1\r\n", "10.0.1")]
    [InlineData("'claude' is not recognized as an internal or external command", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_the_version_out_of_cli_output(string? output, string? expected) =>
        ClaudeCodeVersionDetector.ParseVersion(output).ShouldBe(expected);

    [Fact]
    public void The_fallback_is_a_plain_semantic_version() =>
        ClaudeCodeVersionDetector.ParseVersion(ClaudeCodeVersionDetector.FallbackVersion).ShouldBe(ClaudeCodeVersionDetector.FallbackVersion);
}
