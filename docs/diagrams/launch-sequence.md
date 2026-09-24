# Launch sequence

App launch to first render to background refresh. The flyout opens from cached data in under 150 ms and updates in place when fresh data arrives.

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
    App->>App: Serilog and crash logging
    App->>App: single-instance event + mutex (a second launch signals the first and exits)
    App->>App: build host, DI
    App->>Settings: load settings.json (defaults written on first run)
    App->>Poll: start hosted services (off the UI thread)
    Poll->>Cache: load last snapshot (stale until the first fresh fetch)
    App->>Local: incremental scan + watcher, history recorder, weekly update check
    App->>Settings: settings coordinator starts watching, theme applied
    App->>App: Start with Windows default (first run only)
    App->>Tray: create icon from the current status
    App->>App: warm up the flyout off-screen, load account info

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
