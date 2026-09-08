# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.2] - 2026-09-08

### Added

- An update check: once a week the app asks GitHub whether a newer release exists and says so once in a tray notification. It sends nothing about you, downloads nothing, and can be turned off under Updates in Settings, which also shows the version you are on and links to the releases page.
- winget manifests under `packaging/winget/`, validated locally, plus `docs/packaging.md` covering both the winget submission and the SignPath code-signing setup that would end the SmartScreen prompt.

### Changed

- When the usage endpoint answers with readable JSON that carries no usage windows at all, the app now says the format may have changed and suggests checking for a newer version, instead of showing the same "percentages unavailable" as a network outage. The log records the top-level keys it saw, which is what a diagnosis needs.

### Fixed

- A rapid double refresh (for example a UI double-click) could leave a spare wake-up signal behind and fire one extra fetch on the next cycle, ignoring the manual-refresh floor; the leftover signal is now drained. A `Task.Delay` started for a wait that ended early (a refresh or an options change) no longer keeps running unobserved for up to 30 minutes.
- Today's totals and the daily chart used the offset in force *now* to find local midnight, which is wrong by the DST delta on a transition day; midnight is now computed for the correct offset.
- A `pricing.json` entry missing (or misspelling) `input` or `output` was silently priced at $0 instead of "cost unknown"; such an entry is now skipped, matching how an unrecognized model id is already handled.
- The three main view models were never disposed when the app quit, so a background poll or scan event could still fire while the host was tearing down the SQLite store; they are now disposed before the host stops. A second launch signalling in the last instant of shutdown could throw past a closed flyout window; it is now guarded.
- The "extra usage" progress bar was bound to a `Status` property that does not exist on its data context (silent, no visible effect, but a continuous binding-error trace); it now uses its own style.
- `--capture-settings` silently skipped forcing a solid background because the helper only looked for a `Border` directly under `Window.Content`; it now finds the settings window's root through its `ScrollViewer`.

### Added (tests)

- Regression and gap-filling tests from a full code review: the manual-refresh floor, the DST-safe day boundary, the pricing fallback, `JsonShapeDescriber`'s PII redaction, `UsageAggregator`, `UsageSnapshotFormatter`, `EpochTime`, the JSONL scanner's locked-file path, `SingleInstance`, `TrayIconController.IconPixelSize`, `UsageWindowViewModel`'s status text and sparkline gating, and `TrayIconViewModel.Apply` driven through a real instance.

## [0.1.1] - 2026-09-07

### Added

- Settings to hide the extra-usage line (`showExtraUsage`) and to show the endpoint's inactive codename windows (`showInactiveWindows`).
- An icon of the project's own (a terracotta ring on a dark tile, drawn by `tools/make-icon.py`) for the executable and the settings window.

### Changed

- Defaults: the flyout now starts with the endpoint's numbers only. `showLocalAnalytics` and `showExtraUsage` default to off (`maskEmail` stays on); turn them on in Settings.
- Codename windows the endpoint reports at 0 % with no reset time (for example "Nimbus quill") are hidden by default in the flyout, the tooltip and the history chart; the documented windows are always shown.

## [0.1.0] - 2026-09-07

First release. Everything below is new in this version.

### Added

- Release workflow (`.github/workflows/release.yml`) on `v*` tags: builds and tests the tagged commit, publishes the self-contained single-file exe for win-x64 and win-arm64 with the version taken from the tag, zips each with `pricing.json`, `LICENSE.txt` and a short `README.txt` as `ClaudeUsageTray-<version>-win-<arch>.zip`, writes `SHA256SUMS.txt` and creates the GitHub release with the checksums, this file's section for the version and the generated notes in its body. A re-run for an existing release replaces its assets and notes.
- Settings window (tray menu, or the Settings button in the flyout) backed by `%APPDATA%\ClaudeTrayApp\settings.json`, written with defaults on first run and hot-reloaded: poll interval (floor 180 s, applied to the wait in progress), the window shown in the tray icon, history chart range, history retention with a Clear history action, threshold notifications (off by default, each threshold once per window and period), show or hide local analytics, mask email, theme override, pricing file path. A file that does not parse is left untouched and reported.
- Start with Windows: an opt-in, per-user Run entry, toggled from the tray menu (checkable) or the Settings window.
- One instance per session: a second launch opens the running instance's flyout and exits.
- Windows notifications from the tray icon when a usage window passes a chosen threshold.
- `--capture-settings <png>` development switch.
- Charts in the flyout, drawn by hand from theme brushes (no charting package): the current 5-hour block with its recorded percentages, a dashed projection at the current pace and the projected limit marked when it lands before the reset; utilization history of every window over 24 h, 7 d or 30 d; tokens per day for the last two weeks, split by model on request. Hover readouts, a one-sentence text equivalent per chart, peak-preserving downsampling, and an honest "no data yet" state. Sparklines in the hero and in every window row.
- The flyout re-places itself when its height changes, never grows past the work area (the body scrolls instead), and loads chart data off the UI thread only while it is visible.
- Local analytics from Claude Code session logs: incremental JSONL scanning with per-file offsets in SQLite, deduplication by message id and request id, tolerant of malformed and half-written lines and locked files, near-live updates through a debounced file watcher. Derives today's tokens, per-model and top-project breakdowns, the current 5-hour block's burn rate and, from the endpoint's own percentage, when the limit lands relative to the reset.
- Data-driven pricing: `pricing.json` next to the binary with an effective date shown in the UI; unknown models show "cost unknown", never a number.
- History: every fresh snapshot is recorded per window in `history.db` with 90-day retention and pruning; the aggregator labels the source of every figure.
- Flyout sections for pace and projection, Today with API-equivalent cost and a details disclosure by model and project, and a footer line naming the sources.
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

### Changed

- `AppPaths` honours `LOCALAPPDATA`, `APPDATA` and `USERPROFILE` when the process has them set to rooted paths, falling back to the shell's known folders otherwise, so a run can be pointed at a fresh profile (used to verify the not-signed-in, expired-token and offline states against the published exe).

### Fixed

- Single-file publish failed with IL3000: the Start with Windows wiring fell back to `Assembly.Location`, which is empty in a single-file app. It now falls back to the exe next to `AppContext.BaseDirectory`.
- Core awaits use `ConfigureAwait(false)` and the host starts and stops off the UI thread, so quitting no longer waits five seconds for the polling loop.

[Unreleased]: https://github.com/mlcousek/claude_trayapp/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/mlcousek/claude_trayapp/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/mlcousek/claude_trayapp/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/mlcousek/claude_trayapp/releases/tag/v0.1.0
