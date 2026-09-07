# Data flow

Credential discovery feeds the OAuth provider, whose poller writes every fresh snapshot to the cache and, through the history recorder, to `history.db`. Claude Code session logs feed the local analytics provider, which stores deduplicated usage events and scan offsets in the same database; the calculator prices them on demand. The aggregator merges the two halves for the view models, and the tray icon and flyout render from the merged result.

```mermaid
flowchart LR
    subgraph sources [Sources, read-only]
        CRED["Credential discovery<br/>~/.claude/.credentials.json<br/>(CLAUDE_CONFIG_DIR honoured)"]
        JSONL["Session logs<br/>~/.claude/projects/**/*.jsonl"]
        PRICE["pricing.json<br/>(next to binary, overridable)"]
    end

    subgraph core [ClaudeTrayApp.Core]
        OAUTH["OAuth usage provider + poller<br/>GET /api/oauth/usage<br/>300 s poll, 180 s floor, backoff on 429"]
        LOCAL["JSONL analytics provider<br/>incremental scan, dedupe by message id"]
        CALC["AnalyticsCalculator<br/>today, top projects, 5-hour block, priced"]
        AGG["UsageAggregator<br/>percentages from OAuth (authoritative)<br/>tokens, cost, burn rate from JSONL"]
        HIST[("history.db<br/>SQLite: snapshot series, usage events, scan offsets")]
        CACHE[("cache.json<br/>last snapshot, no token")]
    end

    subgraph app [ClaudeTrayApp, WPF]
        VM["ViewModels"]
        TRAY["Tray icon<br/>generated ring + percent"]
        FLYOUT["Flyout<br/>windows, resets, burn rate, charts"]
    end

    CRED --> OAUTH
    JSONL --> LOCAL
    PRICE --> CALC
    OAUTH -->|every fresh snapshot| HIST
    OAUTH --> CACHE
    CACHE -.->|on launch| OAUTH
    LOCAL -->|events, offsets| HIST
    HIST --> CALC
    OAUTH -->|UsageSnapshot| AGG
    CALC -->|LocalAnalytics| AGG
    AGG --> VM
    HIST -->|chart series| VM
    VM --> TRAY
    VM --> FLYOUT
```
