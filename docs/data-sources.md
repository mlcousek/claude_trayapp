# Data sources

Everything the app shows comes from two sources. Both are read-only. Facts below were probed on a real machine
(Windows 11, Claude Code CLI 2.1.224, 2026-09-07) with every secret value redacted; re-verify before relying on them.

## 1. OAuth usage endpoint (authoritative for percentages and resets)

### Credential discovery

- File: `%USERPROFILE%\.claude\.credentials.json`. If `CLAUDE_CONFIG_DIR` is set, use `<CLAUDE_CONFIG_DIR>\.credentials.json` instead (Claude Code honours the same variable).
- Windows Credential Manager held no Claude entries on the probed machine. Keep discovery pluggable so a keychain source can be added without touching the provider.
- Shape (values redacted):

```json
{
  "claudeAiOauth": {
    "accessToken": "<redacted, 108 chars>",
    "refreshToken": "<redacted, 108 chars>",
    "expiresAt": 1788781207410,
    "refreshTokenExpiresAt": 1790029079410,
    "scopes": ["<redacted>"],
    "subscriptionType": "<plan, e.g. max or pro>",
    "rateLimitTier": "<tier string>"
  },
  "mcpOAuth": { "<server>": "<tokens for MCP servers; the app never reads this block>" }
}
```

- `expiresAt` is Unix epoch milliseconds. Observed access-token lifetime: eight hours. Claude Code refreshes the token when it runs; this app never does (that would mean writing to `~/.claude`).
- When `expiresAt` is in the past or the endpoint answers 401, show "sign in again in Claude Code" and keep serving the last snapshot.
- `subscriptionType` and `rateLimitTier` provide a plan-tier fallback if the endpoint omits one.
- The file is rewritten by Claude Code; re-read it before every poll and tolerate a locked or half-written file (retry next tick).

### Request

```http
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>
anthropic-beta: oauth-2025-04-20
User-Agent: claude-code/<version>
```

- Without the `User-Agent` header requests land in an aggressively rate-limited bucket and stay stuck on 429. The version is read from `claude --version` at runtime (with a timeout) and falls back to a constant in code. On the probed machine the CLI reported 2.1.224 while the desktop app's bundled Claude Code logged 2.1.260.
- The provider logs the response shape once at Debug (token redacted, string values hidden) so a schema drift can be diagnosed from the log; `--probe` logs it with short string values included.

### Response schema (probed live 2026-09-07, values synthetic)

```json
{
  "five_hour":            { "utilization": 51.0, "resets_at": "2026-09-07T16:30:00.961652+00:00", "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null },
  "seven_day":            { "utilization": 9.0,  "resets_at": "2026-09-11T14:00:00.961672+00:00", "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null },
  "seven_day_opus": null, "seven_day_sonnet": null, "seven_day_oauth_apps": null, "seven_day_cowork": null, "seven_day_omelette": null,
  "tangelo": null, "iguana_necktie": null, "omelette_promotional": null, "cinder_cove": null, "copper_kite": null, "amber_ladder": null, "juniper_tide": null,
  "nimbus_quill":         { "utilization": 0.0, "resets_at": null, "limit_dollars": null, "used_dollars": null, "remaining_dollars": null, "locked_reason": null },
  "extra_usage": {
    "is_enabled": true, "monthly_limit": 10000, "used_credits": 0.0, "utilization": null, "currency": "USD", "decimal_places": 2,
    "disabled_reason": null, "user_disabled": false, "spend_limit_reached": false, "credits_ever_enabled": true, "daily": null, "weekly": null
  },
  "limits": [ { "kind": "session", "group": "session", "percent": 51, "severity": "warning", "resets_at": "...", "scope": null, "is_active": true } ],
  "spend": {
    "used":  { "amount_minor": 0,     "currency": "USD", "exponent": 2 },
    "limit": { "amount_minor": 10000, "currency": "USD", "exponent": 2 },
    "percent": 0, "severity": "normal", "enabled": true, "disabled_reason": null,
    "cap": { "money": null, "credits": { "amount_minor": 10000, "exponent": 2 } },
    "balance": null, "auto_reload": null, "disclaimer": "<113 chars>", "can_purchase_credits": false, "can_toggle": false
  },
  "member_dashboard_available": false
}
```

What the parser makes of it:

- Every top-level object with a `utilization` number is a window. `utilization` is a percentage on a 0 to 100 scale (float). Windows that do not apply to the account are `null` and skipped. Besides the documented `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet` and `seven_day_oauth_apps`, the response carries codename windows (`nimbus_quill`, `tangelo`, ...); when one is non-null it renders like any unknown key, sorted after the known ones.
- `resets_at` is ISO-8601 with microseconds and an explicit offset; it is `null` while a window is unused.
- `locked_reason` is a string when a window is locked; the UI must show it, whatever the percentage says.
- `extra_usage` amounts (`monthly_limit`, `used_credits`) are in minor units; divide by `10^decimal_places`. The `spend` block repeats the same money as `amount_minor` plus `exponent` and fills any value `extra_usage` leaves null. Without either block there is no overage.
- `limits` is a parallel, self-describing list (`kind`, `group`, `percent`, `severity`, `resets_at`, `scope`, `is_active`). Three entries were observed: the active session limit (`kind` 7 chars, `severity` `warning` at 86 % and an 8-character value at 100 %), and two inactive weekly-style entries, one of them with `scope.model` set, so per-model weekly limits live here. It is not parsed yet because the `kind` to window mapping is unverified; `severity` shows Anthropic's own thresholds sit below this app's 70/90 defaults. Revisit when the mapping is confirmed.
- No plan tier is present in the body; `subscriptionType` from the credentials file is used instead.
- Response time was well under a second; one request per poll.

### Related local caches (not used yet)

`%USERPROFILE%\.claude.json` (a file, distinct from the `.claude` folder) holds `oauthAccount` with `emailAddress`, `displayName`, `organizationName`, `hasExtraUsageEnabled`, `billingType` and `organizationRateLimitTier`, which the flyout header can read (M4). It also holds `cachedUsageUtilization` (`fetchedAtMs` plus the response above under `utilization`), Claude Code's own cache, which could serve as an offline fallback but is often days old. Both are read-only for this app; the file also contains unrelated settings and must never be written.

### Polling discipline

Default interval 300 s, configurable with a hard floor of 180 s. On 429: exponential backoff capped at 30 minutes, cached snapshot served with a visible "stale" indicator. Manual refresh is debounced against the same limiter. The last snapshot is persisted to `%LOCALAPPDATA%\ClaudeTrayApp\cache.json` (never the token) so the app shows data immediately on launch.

## 2. Claude Code session logs (local analytics, works offline)

Location: `<ClaudeHome>\projects\<encoded-project-path>\<session-id>.jsonl`, plus nested folders for subagent sessions. One JSON object per line.

Volume on the probed machine: 527 files, 452 MB, 12 project folders, largest file 40 MB. A full re-scan on every start is not acceptable; scanning is incremental with per-file byte offsets and mtimes stored in SQLite, and a debounced `FileSystemWatcher` provides near-live updates.

Record types seen in the first lines of a live session: `user`, `assistant`, `attachment`, `atis-latch`, `custom-title`, `last-prompt`, `bridge-session`, `queue-operation`. Only `assistant` records carry token usage. Unknown types are skipped silently.

### Assistant record (keys verified, values synthetic)

```json
{
  "type": "assistant",
  "uuid": "00000000-0000-0000-0000-000000000001",
  "parentUuid": "00000000-0000-0000-0000-000000000000",
  "isSidechain": false,
  "timestamp": "2026-09-07T05:44:13.000Z",
  "sessionId": "00000000-0000-0000-0000-0000000000aa",
  "requestId": "req_example",
  "cwd": "C:\\path\\to\\project",
  "version": "2.1.260",
  "gitBranch": "main",
  "apiBlockIndex": 0,
  "message": {
    "id": "msg_example",
    "type": "message",
    "role": "assistant",
    "model": "claude-fable-5-1",
    "content": [{ "type": "text", "text": "..." }],
    "stop_reason": "tool_use",
    "usage": {
      "input_tokens": 2,
      "cache_creation_input_tokens": 32271,
      "cache_read_input_tokens": 42113,
      "output_tokens": 564,
      "cache_creation": { "ephemeral_5m_input_tokens": 0, "ephemeral_1h_input_tokens": 32271 },
      "output_tokens_details": { "thinking_tokens": 293 },
      "service_tier": "standard",
      "iterations": [{ "input_tokens": 2, "output_tokens": 564, "cache_read_input_tokens": 42113, "cache_creation_input_tokens": 32271 }]
    }
  }
}
```

Rules derived from the probe:

- **Dedupe by `message.id` plus `requestId`.** One API response is written as several assistant lines, one per content block (`apiBlockIndex` increments), and every line repeats the same `usage`. Count each message id once or costs are overcounted several-fold.
- Cache writes split into five-minute and one-hour buckets under `usage.cache_creation`; they are priced differently. If the split is absent, treat `cache_creation_input_tokens` as five-minute writes.
- `isSidechain: true` marks subagent traffic. It still consumes the account's limits and is counted.
- `timestamp` is ISO-8601 UTC. `cwd` identifies the project; the folder name under `projects/` is an encoded form of the same path.
- Malformed or partial lines occur while Claude Code is writing. Skip the line and keep the stored offset at the last complete newline so the tail is re-read next time.
- Files can be locked briefly; open with read sharing and retry on the next tick, never crash.
- `message.model` is the API model id. Pricing lookup is exact id first, then prefix family match, then "unknown".

## 3. Pricing data

`pricing.json` ships next to the binary and can be overridden from settings. Prices are USD per million tokens and the UI shows `effectiveDate`.

```json
{
  "effectiveDate": "2026-09-01",
  "currency": "USD",
  "models": [
    { "id": "claude-fable-5-1", "match": "exact", "input": 0, "output": 0, "cacheWrite5m": 0, "cacheWrite1h": 0, "cacheRead": 0 }
  ]
}
```

Unknown model ids degrade to "cost unknown", never to zero or a guessed number. The shipped file is filled in and verified in M5.
