# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Flyout: an acrylic tool window anchored to the taskbar edge next to the icon, DPI aware (PerMonitorV2 manifest), dismissed by click-outside or Esc, never in Alt-Tab, warmed up so it opens in under 150 ms. The 5-hour window is the hero: a large Claude-coloured ring with the percentage, a plain-language status, the reset countdown and clock time; other windows are compact rows; extra usage, status banner, honest "percentages unavailable" state, and a footer with relative time, Refresh and Settings.
- Account header from Claude Code's `.claude.json` (read-only): plan tier and the email, masked by default with a Show/Hide toggle.
- `--capture-flyout <png>` development switch for screenshots without the account line.
- Tray icon rendered at runtime: ring-arc of the primary window around a compact numeral, colour by status, dimmed when stale, redrawn on data, DPI and theme changes, legible at 16 px on light and dark taskbars.
- Right-click menu with Refresh (debounced, explains refusals in a notification), Open logs, About and Quit; Settings and Start with Windows are placeholders until milestone 7.
- Theme tokens (`Theme.xaml`) with dark and light palettes that follow the Windows apps and taskbar settings.
- Crash logging for UI-thread, app-domain and unobserved task exceptions.
- `--render-icons <dir>` development switch that writes icon contact sheets.
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

### Fixed

- Core awaits use `ConfigureAwait(false)` and the host starts and stops off the UI thread, so quitting no longer waits five seconds for the polling loop.

[Unreleased]: https://github.com/mlcousek/claude_trayapp/compare/0c88ac1...HEAD
