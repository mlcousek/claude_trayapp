# CLAUDE.md

Working brief for Claude Code sessions in this repository. Factual, no marketing, under ~150 lines.

## Project

Claude Usage Tray is a minimalist Windows 11 system-tray app that shows the current Claude usage limits at a glance:
a live percentage rendered into the tray icon, and a dark flyout with every usage window, reset countdowns, plan tier,
burn rate, history charts and local token/cost analytics derived from Claude Code session logs.
It is an unofficial community tool, not affiliated with or endorsed by Anthropic. It reads the Claude Code OAuth token
only to call the same usage endpoint Claude Code itself uses, and it never writes to `~/.claude`.

## Commands (verified on Windows 11, SDK 9.0.311)

```bash
dotnet restore
dotnet build --configuration Release            # warnings are errors in Release
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes               # CI fails on any diff
dotnet run --project src/ClaudeTrayApp
dotnet run --project src/ClaudeTrayApp -- --probe    # one live fetch, redacted summary in the log, exit 0 or 2
dotnet run --project src/ClaudeTrayApp -- --render-icons out/icons   # tray icon contact sheets (PNG) for a legibility check
dotnet run --project src/ClaudeTrayApp -- --capture-flyout out/flyout.png   # opens the flyout after the first poll, screenshots it, exits
dotnet publish src/ClaudeTrayApp -c Release -r win-x64 -o artifacts/publish/win-x64
dotnet publish src/ClaudeTrayApp -c Release -r win-arm64 -o artifacts/publish/win-arm64
```

Run from the repo root: `global.json` pins the 9.0.3xx SDK band because dev machines may default to a newer preview SDK.
Publish output is a single self-contained, ReadyToRun exe (about 62 MB); `artifacts/` is git-ignored.

## Layout

- `src/ClaudeTrayApp/` WPF app: composition root (`App.xaml.cs`), `Themes/` (`Theme.xaml` tokens, `Controls.xaml` styles, `Palette.Dark.xaml`, `Palette.Light.xaml`), `Theming/` (Windows theme follower), `Tray/` (icon renderer, controller, converter, flyout placement maths), `Views/` (`FlyoutWindow`), `Controls/` (`RingArc`, `Sparkline`, `LineChart`, `BarChart`: hand-drawn, theme-brushed), `Charts/` (pure chart data builders, loader, palette), `ViewModels/`, `Hosting/`, `Interop/`, `Diagnostics/` (screenshot aid). The only project that references WPF.
- `tests/ClaudeTrayApp.Tests/` WPF-side tests (net9.0-windows): tray state, tooltip, renderer pixels on an STA thread, chart data builders, charts view model.
- `src/ClaudeTrayApp.Core/` `Domain/` (snapshot, windows, humaniser), `Credentials/`, `Providers/` (OAuth endpoint, parser), `Polling/` (state machine, poller), `Cache/`, `Analytics/` (JSONL line parser, incremental scanner, provider with watcher, calculator), `Storage/` (SQLite store: events, scan offsets, history), `Pricing/` (`pricing.json` loader and table), `History/` (recorder, retention), `Aggregation/`, `Account/`, `Security/` (redactor), `Diagnostics/`, `ClaudeCode/` (version detection). No UI references, ever.
- `tests/ClaudeTrayApp.Core.Tests/` xunit.v3 + Shouldly + NSubstitute. Fixture files with fake tokens and synthetic sessions only.
- `docs/` `architecture.md`, `data-sources.md`, `diagrams/` (Mermaid sources), `screenshots/`.
- `.github/` `workflows/ci.yml` (build, test, format), `workflows/release.yml` (M8), Dependabot, issue templates.
- `Directory.Build.props` shared MSBuild settings. `Directory.Packages.props` every package version. `global.json` SDK pin.

## Architecture

- Providers produce a `UsageSnapshot` (percentages, resets, tier) or local analytics (tokens, cost, burn rate). They know about transport, never about UI.
- `UsageAggregator` merges providers: OAuth data is authoritative for percentages, JSONL data for token and cost analytics. Every field carries its source.
- `SqliteStore` (`history.db`) holds usage events deduplicated by message id and request id, per-file scan offsets, and the snapshot time series; `HistoryRecorder` appends every fresh snapshot and prunes past the retention window. Charts read only from it.
- `JsonlScanner` reads only bytes appended since the last scan (530 files, 29k events, about 4 s on first run, under 100 ms after); `LocalAnalyticsProvider` rescans on a debounced file watcher and a 5-minute safety net; `AnalyticsCalculator` derives today, top projects and the 5-hour block on demand, pricing from `pricing.json`.
- ViewModels (CommunityToolkit.Mvvm source generators) adapt aggregated data for binding; views bind and draw, never compute.
- Charts are drawn by three small `FrameworkElement`s in `Controls/` from theme brushes; there is no charting package. `Charts/ChartDataBuilder` turns history rows into series (peak-preserving downsampling, projection clipped at the reset, daily totals with the long tail as "other") and is pure; `ChartDataLoader` runs the SQLite queries on a thread-pool thread; `ChartsViewModel` hands the controls points and brushes. Every chart carries a text summary as its automation name and caption.
- Dependency direction is one way: `ClaudeTrayApp` references `ClaudeTrayApp.Core`. Core never references WPF; `CoreArchitectureTests` fails the build if it does.

## Hard rules

- Never log, print, persist or commit a token. Redact `Authorization` headers and anything token-shaped before logging. Tests use fake tokens.
- Never write to `~/.claude` or `CLAUDE_CONFIG_DIR`. Read-only, always. The app never refreshes the OAuth token; on expiry it asks the user to open Claude Code.
- Never poll the usage endpoint below the 180 s floor. Default 300 s. On 429 back off exponentially, capped at 30 min, and keep serving the cached snapshot marked stale.
- Never fabricate a percentage when the endpoint is unavailable. Show "percentages unavailable" plus local analytics.
- Pricing is never hardcoded: `pricing.json` next to the binary, overridable in settings, effective date shown in the UI. Unknown model ids render "cost unknown", never a number.
- Only permissive-licensed dependencies (MIT, Apache-2.0, BSD). Ask before adding any package not already in `Directory.Packages.props`. LiveChartsCore was dropped in M6: its WPF view pulls OpenTK and SkiaSharp.Views.WPF built for .NET Framework (NU1701) and the charts here are simple enough to draw by hand.
- No telemetry of any kind. The only network destination is `api.anthropic.com`.

## Conventions

- Conventional Commits. Small, reviewable commits. Never commit generated artifacts, user data or anything from `~/.claude`.
- Nullable enabled everywhere; `TreatWarningsAsErrors` in Release; analyzers at `latest-recommended`; `.editorconfig` is enforced by `dotnet format`.
- MVVM via CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`). No hand-rolled `INotifyPropertyChanged`.
- Theme tokens only: colours, brushes and fonts come from `Theme.xaml`. No literals in views. Light and dark palettes both ship. The `Ok` status colour is the Claude accent (terracotta); amber and red take over as a window fills, and every status is also written as text.
- Core is a library: every `await` uses `ConfigureAwait(false)` (CA2007 is a warning under `src/ClaudeTrayApp.Core`). The app starts and stops the host off the UI thread; view models marshal to the dispatcher themselves.
- The flyout is hidden, never closed; it is warmed up off-screen at start so a real open takes under 150 ms. Chart data is loaded once at warm-up and then only while the flyout is visible; loads never run on the UI thread. The window re-places itself when its height changes and never exceeds the work area (the body scrolls instead).
- Tests are required for anything in Core. Test names use underscores (CA1707 is off under `tests/`).
- Accessibility is part of done: keyboard navigation, `AutomationProperties` on every control, contrast at least 4.5:1, never colour alone.

## Known fragilities

- `GET https://api.anthropic.com/api/oauth/usage` is undocumented. Its shape can change and it rate-limits aggressively. The `User-Agent: claude-code/<version>` header matters; without it requests land in a throttled bucket.
- Credential location varies. Today: `%USERPROFILE%\.claude\.credentials.json`, key `claudeAiOauth.accessToken`, honouring `CLAUDE_CONFIG_DIR`. The same file holds an unrelated `mcpOAuth` block; parse only `claudeAiOauth`.
- Access tokens live about eight hours; Claude Code refreshes them when used. Expect 401 and show a re-authenticate hint instead of failing.
- JSONL session logs are not a contract. One API response is written as several `assistant` lines sharing `message.id` and `requestId`; dedupe them or costs are overcounted several-fold.
- The Claude Code CLI on PATH and the desktop app's bundled Claude Code can report different versions.
- Extra-usage amounts arrive in minor units (`decimal_places`, or `amount_minor` plus `exponent` in the `spend` block). Codename windows such as `nimbus_quill` appear alongside the documented ones and render like any unknown key.
- The endpoint's own `limits[].severity` said `warning` at 86 %, below this app's 70/90 status thresholds; the mapping from `limits[].kind` to window keys is unverified, so `limits` is not parsed yet.
- H.NotifyIcon.Wpf 2.4 dropped net9.0-windows; stay on 2.3.x (Dependabot is told so). Its `IconSource` path rejects `RenderTargetBitmap`, so the tray icon is converted to a `System.Drawing.Icon` by `IconConverter` and set through `TaskbarIcon.Icon`.
- The tray icon is rendered at the system DPI (16/20/24/32 px). Per-monitor DPI for the taskbar is not tracked; a DPI change triggers a redraw through `SystemEvents.DisplaySettingsChanged`.

## Before you start

1. Read `docs/data-sources.md` (probed schemas) and `docs/architecture.md` (layers and diagrams).
2. Run `git status`; it must be clean. Work on `main` in small commits (single-developer repo, pushes go straight to main).
3. `dotnet build -c Release && dotnet test -c Release --no-build && dotnet format --verify-no-changes` must pass before every commit.
4. Update this file, `docs/` and the diagrams whenever anything they describe changes. A wrong diagram is worse than none.

## Milestones

M1 scaffold (done) · M2 core domain, OAuth provider, cache, backoff (done, live probe verified 2026-09-07) · M3 tray icon (done) · M4 flyout (done) · M5 JSONL analytics, history, aggregation (done) · M6 charts (done) · M7 settings, autostart, notifications, single instance · M8 release pipeline, docs, v0.1.0
