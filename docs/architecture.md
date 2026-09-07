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

Dependency direction is one way: `ClaudeTrayApp` references `ClaudeTrayApp.Core`. Core has no reference to WPF,
H.NotifyIcon or LiveCharts; `tests/ClaudeTrayApp.Core.Tests/CoreArchitectureTests.cs` fails the build otherwise.

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

    UI["WPF, H.NotifyIcon.Wpf, CommunityToolkit.Mvvm,<br/>LiveChartsCore.SkiaSharpView.WPF"]
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
    participant Cache as cache.json
    participant Tray as Tray icon
    participant Poll as Polling loop
    participant API as api.anthropic.com
    participant Local as JSONL analytics

    U->>App: launch (single-instance check)
    App->>App: build host, Serilog, DI
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

Milestones 1 and 2 are delivered: solution layout, build settings, CI, `AppPaths`, the composition root with file
logging, the usage domain, credential discovery, the OAuth usage provider, the polling state machine and the
snapshot cache, verified against the live endpoint. The tray icon, flyout, JSONL analytics provider, aggregator,
history store and charts arrive in milestones 3 to 7 and this document is updated with each of them.
