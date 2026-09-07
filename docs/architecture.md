# Architecture

Claude Usage Tray is a WPF tray app with one strict split: everything that talks to the outside world or does
arithmetic lives in `ClaudeTrayApp.Core`; the WPF project only composes, binds and draws.

## Layers

| Layer | Project | Responsibility |
|---|---|---|
| Providers | Core | Turn a data source into a `UsageSnapshot` (OAuth endpoint) or local analytics (JSONL logs). Know about transport, never about UI. |
| Aggregator | Core | Merge provider outputs, label the source of every field, decide the stale and unavailable states. Never fabricates a percentage. |
| History store | Core | SQLite time series of snapshots and daily token/cost rollups, plus JSONL scan offsets and retention pruning. |
| ViewModels | App | Observable adapters over aggregated data via CommunityToolkit.Mvvm source generators. Countdown text and number formatting live here. |
| Views | App | XAML bound to viewmodels. Colours, brushes and fonts come from `Theme.xaml` tokens only. |

Dependency direction is one way: `ClaudeTrayApp` references `ClaudeTrayApp.Core`. Core has no reference to WPF or
H.NotifyIcon; `tests/ClaudeTrayApp.Core.Tests/CoreArchitectureTests.cs` fails the build otherwise.

## Runtime folders

All paths are resolved by `AppPaths` from environment folders. Nothing is hardcoded to a user, and
`CLAUDE_CONFIG_DIR` relocates the Claude Code home exactly as it does for Claude Code.

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\ClaudeTrayApp\cache.json` | Last successful snapshot. Never contains a token. |
| `%LOCALAPPDATA%\ClaudeTrayApp\history.db` | SQLite history, daily rollups, JSONL scan offsets. |
| `%LOCALAPPDATA%\ClaudeTrayApp\logs\` | Rolling daily logs, seven days, five MB each. Tokens redacted. |
| `%APPDATA%\ClaudeTrayApp\settings.json` | User settings, hot-reloaded. |
| `%USERPROFILE%\.claude\` | Claude Code home. Read-only for this app. |

## Diagrams

Sources live in [`diagrams/`](diagrams/). Edit the source file and this embedded copy together; a wrong diagram
is worse than none.

### Data flow

```mermaid
flowchart LR
    subgraph sources [Sources, read-only]
        CRED["Credential discovery<br/>~/.claude/.credentials.json<br/>(CLAUDE_CONFIG_DIR honoured)"]
        JSONL["Session logs<br/>~/.claude/projects/**/*.jsonl"]
        PRICE["pricing.json<br/>(next to binary, overridable)"]
    end

    subgraph core [ClaudeTrayApp.Core]
        OAUTH["OAuth usage provider<br/>GET /api/oauth/usage<br/>300 s poll, 180 s floor, backoff on 429"]
        LOCAL["JSONL analytics provider<br/>incremental scan, dedupe by message id"]
        AGG["UsageAggregator<br/>percentages from OAuth (authoritative)<br/>tokens, cost, burn rate from JSONL"]
        HIST[("History store<br/>SQLite: snapshots, daily rollups, scan offsets")]
        CACHE[("cache.json<br/>last snapshot, no token")]
    end

    subgraph app [ClaudeTrayApp, WPF]
        VM["ViewModels"]
        TRAY["Tray icon<br/>generated ring + percent"]
        FLYOUT["Flyout<br/>windows, resets, burn rate, charts"]
    end

    CRED --> OAUTH
    JSONL --> LOCAL
    PRICE --> LOCAL
    OAUTH -->|UsageSnapshot| AGG
    LOCAL -->|LocalAnalytics| AGG
    AGG --> HIST
    AGG --> CACHE
    CACHE -.->|on launch| AGG
    AGG --> VM
    HIST -->|chart series| VM
    VM --> TRAY
    VM --> FLYOUT
```

### Polling state machine

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Fetching : tick (interval >= 180 s) or debounced manual refresh
    Fetching --> Ok : 200, parsed
    Fetching --> RateLimited : 429
    Fetching --> Unauthenticated : 401, missing credentials, or token past expiresAt
    Fetching --> Stale : network error, timeout, 5xx, unparseable body

    Ok --> Idle : write cache.json + history, reset backoff, clear stale flag
    Stale --> Idle : keep last snapshot, show "stale", backoff = min(2^n x interval, 30 min)
    RateLimited --> Idle : keep last snapshot, show "rate limited", backoff = min(2^n x interval, 30 min)
    Unauthenticated --> Idle : show "open Claude Code to sign in", re-read credentials next tick

    note right of Unauthenticated
        The app never refreshes tokens.
        Claude Code does that when it runs.
    end note
```

### Components

```mermaid
flowchart TB
    APP["ClaudeTrayApp (WPF, net9.0-windows)<br/>composition root, views, viewmodels, Theme.xaml"]
    CORE["ClaudeTrayApp.Core (net9.0)<br/>domain, providers, aggregator, parsing, pricing, history"]
    TESTS["ClaudeTrayApp.Core.Tests (xunit.v3)<br/>fixtures with fake tokens only"]

    UI["WPF, H.NotifyIcon.Wpf, CommunityToolkit.Mvvm"]
    BCL["Microsoft.Extensions.*, Microsoft.Data.Sqlite,<br/>System.Text.Json"]

    APP --> CORE
    TESTS --> CORE
    APP --> UI
    APP --> BCL
    CORE --> BCL

    classDef forbidden stroke-dasharray: 5 5
    CORE -. "never" .-> UI
    linkStyle 5 stroke-dasharray: 5 5
```

### Launch sequence

```mermaid
sequenceDiagram
    actor U as User
    participant App as App (composition root)
    participant Settings as settings.json
    participant Cache as cache.json
    participant Tray as Tray icon
    participant Poll as Polling loop
    participant API as api.anthropic.com
    participant Local as JSONL analytics

    U->>App: launch
    App->>App: single-instance mutex (a second launch signals the first and exits)
    App->>App: build host, Serilog, DI
    App->>Settings: load settings.json (defaults written on first run), start watching
    App->>Cache: load last snapshot
    Cache-->>App: snapshot or none
    App->>Tray: render icon from cache (stale flag if old)
    App->>Poll: start
    App->>Local: start incremental scan + watcher

    Poll->>API: GET /api/oauth/usage
    API-->>Poll: 200 snapshot (or 429 / 401 / error)
    Poll->>Cache: persist snapshot
    Poll->>Tray: update icon and tooltip

    U->>Tray: left-click
    Tray->>App: open flyout from cache
    App-->>U: flyout visible (< 150 ms)
    Poll-->>App: fresh data
    App-->>U: flyout updates in place
```

## Status

Milestones 1 to 7 are delivered: solution layout, build settings, CI, `AppPaths`, the composition root with file
logging and crash logging, the usage domain, credential discovery, the OAuth usage provider, the polling state
machine, the snapshot cache (verified against the live endpoint), theme tokens with dark and light palettes, the
generated tray icon with its context menu, the flyout, the local analytics pipeline with the SQLite history store
and the aggregator, the charts, and settings with threshold notifications, autostart and single instance. The
release pipeline arrives in milestone 8.

## Local analytics pipeline

`LocalAnalyticsProvider` runs as a hosted service: one incremental scan at start, a rescan two seconds after the
session logs change (debounced `FileSystemWatcher`), and a safety-net rescan every five minutes. `JsonlScanner`
keeps a byte offset, length and mtime per file in `SqliteStore`, reads only appended bytes, leaves a trailing
partial line for the next pass, restarts a shrunken file from zero, and skips locked files until the next pass.
`JsonlLineParser` turns assistant records into `UsageEvent`s; the store's primary key on (message id, request id)
deduplicates the several lines one API response produces. On the probed machine the first scan of 530 files
(452 MB) stored 29,063 events in 4.3 s; later scans take under 100 ms. `AnalyticsCalculator` answers questions
on demand from SQL aggregates: today's totals by model priced through `PricingTable`, top projects, and the
current 5-hour block anchored on the endpoint's reset time, with a projection computed only from the endpoint's
percentage. `HistoryRecorder` appends every fresh snapshot per window and prunes past the retention window once a
day. `UsageAggregator` hands the flyout one object whose two halves name their sources.

## Flyout

`FlyoutWindow` is a WPF tool window (`WS_EX_TOOLWINDOW`, no taskbar button, not in Alt-Tab) created once at start
and warmed up off-screen; opening it is a show plus placement, measured at about 100 ms. Placement is pure maths in
`FlyoutPlacement`: the monitor under the cursor and its work area decide the taskbar edge, and the window is moved
with `SetWindowPos` in physical pixels, so mixed-DPI setups work with the PerMonitorV2 manifest. The backdrop is
DWM's transient-window acrylic when available, with a solid surface fallback. Click-outside is handled by the
window's `Deactivated` event and Esc by key handling. `FlyoutViewModel` splits the snapshot into the primary window
(the hero ring, `RingArc`) and the secondary rows, formats every string, and re-renders time-dependent text every
30 s while the flyout is visible.

## Charts

There is no charting package. `Controls/Sparkline`, `Controls/LineChart` and `Controls/BarChart` are small
`FrameworkElement`s that draw with a `DrawingContext` from the brushes they are given, so they follow the palette
like every other view: one hairline baseline, edge labels, a top value, optional vertical markers, and a hover
readout drawn only while the pointer is over the chart. `Charts/ChartDataBuilder` is pure: it downsamples a series
to at most 240 points keeping the highest value per bucket (peaks survive), builds the 5-hour block chart from the
recorded percentages plus a straight projection at the calculator's pace that stops at the reset, and rolls daily
token totals into the top four models plus "other". `Charts/ChartDataLoader` runs the SQLite queries on a
thread-pool thread; `ChartsViewModel` applies the result on the dispatcher and exposes one chart at a time (this
block, history over 24 h, 7 d or 30 d, daily tokens with a by-model toggle). Every chart also produces a sentence,
used as its automation name and shown under it, and a chart without data collapses to that sentence. Sparklines
sit in the hero (the current block) and in each window row (its own period); they stay hidden until readings
cover at least 5 % of the period.

## Settings, notifications and startup

`SettingsStore` (Core) owns `%APPDATA%\ClaudeTrayApp\settings.json`. It writes the defaults on first run so the
file is there to edit, saves atomically (temp file plus move), normalises every value on the way in (the poll floor,
retention bounds, sorted thresholds), and reloads on external edits through a debounced `FileSystemWatcher`,
ignoring the echo of its own writes. A file that does not parse is left untouched and reported through
`LastError`; the previous settings stay in force. `SettingsCoordinator` (app) applies the current settings once at
start and then every change: `UsagePoller.UpdateOptions` re-times a wait in progress from the last attempt (a
backoff keeps its plan), `HistoryOptions` and the scanner's age cutoff follow the retention, `PricingProvider`
reloads when the pricing path changes, and `ThemeManager.Override` follows the theme setting. The flyout, tray
and settings view models subscribe to the store themselves and marshal to the dispatcher; the flyout writes back
its two toggles (email mask, chart range) so the file always says what the screen shows.

`ThresholdNotifier` (Core) is pure: given a snapshot and the enabled thresholds it returns the crossings not yet
announced, keyed by window and period (the window's reset time), reporting only the highest threshold when several
are crossed at once. `TrayIconViewModel` evaluates it on every fresh snapshot and shows the result as a Windows
notification through the tray icon.

`AutostartManager` reads and writes the per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value
for the running executable; it is not in settings.json because the registry is the truth. `SingleInstance` holds a
named mutex; a second launch signals a named event and exits, and the first instance shows its flyout.

## Threading

Core is a library and every `await` in it uses `ConfigureAwait(false)`; CA2007 enforces that under
`src/ClaudeTrayApp.Core`. The app starts and stops the generic host off the UI thread, so the polling loop never
inherits the dispatcher context, and view models marshal `StatusChanged` to the dispatcher themselves.

## Tray icon pipeline

`UsagePoller.StatusChanged` (background thread) → `TrayIconViewModel.Apply` on the dispatcher → `TrayIconState`
(percent, status, stale) and tooltip → `TrayIconController.Redraw` → `TrayIconRenderer.Render` at the system DPI
pixel size → `IconConverter.ToIcon` → `TaskbarIcon.Icon`. Theme and display changes re-enter at `Redraw`.
