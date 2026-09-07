# Polling state machine

The OAuth provider's loop. Every transition back to Idle schedules the next tick; the delay is the configured interval (floor 180 s) or the current backoff, whichever is longer. Manual refresh is debounced through the same limiter and can never bypass a backoff.

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
