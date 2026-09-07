# Contributing

Thanks for helping. This is a small, single-maintainer project, so the process is light but the rules below are firm.

## Setup

1. Install the .NET 9 SDK (any 9.0.3xx band; `global.json` picks it).
2. Clone and build:

```bash
git clone https://github.com/mlcousek/claude_trayapp.git
cd claude_trayapp
dotnet build --configuration Release
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes
```

Those three commands are exactly what CI runs. If they pass locally they pass in CI.

## Rules

- **Secrets.** Never log, print, persist or commit a token. Test fixtures use fake tokens and synthetic session logs, never real data from `~/.claude`.
- **Read-only.** The app never writes to `~/.claude` (or `CLAUDE_CONFIG_DIR`).
- **Polling.** Never poll the usage endpoint below 180 seconds. Never fabricate a percentage when it is unavailable.
- **Pricing.** Never hardcode model prices; edit `pricing.json`.
- **Dependencies.** Permissive licences only (MIT, Apache-2.0, BSD). Open an issue before adding a package.
- **Core stays UI-free.** `ClaudeTrayApp.Core` must not reference WPF or any UI library; a test enforces it.
- **Tests.** Anything in Core needs tests. Test names use underscores.
- **Style.** `.editorconfig` is the law; run `dotnet format` before committing.

## Commits and pull requests

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `docs:`, `ci:`, `chore:`, `refactor:`, `test:`, `build:`.
- Keep commits small and reviewable. Do not commit generated artifacts, user data, screenshots containing an account email, or anything from `~/.claude`.
- Update `CLAUDE.md`, `docs/` and the Mermaid diagrams whenever you change behaviour they describe.
- Add a line under `[Unreleased]` in `CHANGELOG.md`.
- Releases are cut by the maintainer: a `v<major>.<minor>.<patch>` tag pushed from a green `main` runs the release workflow, which publishes, zips, checksums and publishes the GitHub release (see the Release section of `CLAUDE.md`).

## Reporting bugs

Use the bug template. Logs live in `%LOCALAPPDATA%\ClaudeTrayApp\logs`; the app redacts tokens, but check before attaching. Security issues go through [SECURITY.md](SECURITY.md), not public issues.
