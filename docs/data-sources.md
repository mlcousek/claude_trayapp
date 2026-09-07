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
- Response schema: captured in M2. The provider logs the response shape once at Debug (token redacted) so the schema can be adapted; unknown window keys still render with a humanised name derived from the key.

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
