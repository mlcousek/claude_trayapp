# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Usage domain model (`UsageSnapshot`, `UsageWindow`, `OverageInfo`) with humanised window names, status thresholds and locked-window support.
- Credential discovery from Claude Code's credentials file: read-only, honours `CLAUDE_CONFIG_DIR`, parses only the Claude OAuth section and never the MCP tokens, detects expiry.
- OAuth usage provider sending the headers Claude Code sends, with defensive schema parsing (unknown windows still render, extra usage scaled from minor units) and a one-time redacted shape log.
- Polling state machine: 180 s floor, exponential backoff capped at 30 minutes, Retry-After honoured, debounced manual refresh, cached snapshot served on launch.
- `--probe` switch: one live fetch, redacted summary in the log, exit code 0 or 2.
- Solution scaffold: `ClaudeTrayApp.Core` (net9.0), `ClaudeTrayApp` (WPF, net9.0-windows) and xunit.v3 tests with Shouldly and NSubstitute.
- Shared build settings (`Directory.Build.props`), central package management, `global.json` SDK pin, `.editorconfig` enforced by `dotnet format`.
- `AppPaths`: resolves app folders and the Claude Code home from the environment, honouring `CLAUDE_CONFIG_DIR`.
- Composition root with generic host and Serilog rolling file logs in `%LOCALAPPDATA%\ClaudeTrayApp\logs`.
- Architecture test that fails the build if Core references a UI assembly.
- CI workflow (build, test, format check on Windows), Dependabot, issue templates.
- Documentation: `CLAUDE.md`, `docs/architecture.md`, `docs/data-sources.md`, Mermaid diagrams.

[Unreleased]: https://github.com/mlcousek/claude_trayapp/compare/0c88ac1...HEAD
