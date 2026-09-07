# Security policy

## Reporting a vulnerability

Please do not open a public issue for security problems. Use GitHub's private vulnerability reporting:

https://github.com/mlcousek/claude_trayapp/security/advisories/new

Include steps to reproduce and the app version. You should get an acknowledgement within a week. Fixes ship as a patch release with a note in `CHANGELOG.md`; credit is given unless you prefer otherwise.

## Scope

Reports are especially welcome for anything touching:

- OAuth token handling: the token must never be logged, persisted by this app, or sent anywhere except `api.anthropic.com`.
- File access: the app must only read from `~/.claude` and only write under `%LOCALAPPDATA%\ClaudeTrayApp` and `%APPDATA%\ClaudeTrayApp`.
- Network behaviour: polling floors and backoff must hold; no other endpoints may be contacted.
- Startup registration (`HKCU\...\Run`) and settings hot-reload.

## Supported versions

Only the latest release receives fixes.

## What this app is not

It is an unofficial community tool with no affiliation to Anthropic. Vulnerabilities in Claude Code or Anthropic services should be reported to Anthropic, not here.
