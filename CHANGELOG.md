# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Solution scaffold: `ClaudeTrayApp.Core` (net9.0), `ClaudeTrayApp` (WPF, net9.0-windows) and xunit.v3 tests with Shouldly and NSubstitute.
- Shared build settings (`Directory.Build.props`), central package management, `global.json` SDK pin, `.editorconfig` enforced by `dotnet format`.
- `AppPaths`: resolves app folders and the Claude Code home from the environment, honouring `CLAUDE_CONFIG_DIR`.
- Composition root with generic host and Serilog rolling file logs in `%LOCALAPPDATA%\ClaudeTrayApp\logs`.
- Architecture test that fails the build if Core references a UI assembly.
- CI workflow (build, test, format check on Windows), Dependabot, issue templates.
- Documentation: `CLAUDE.md`, `docs/architecture.md`, `docs/data-sources.md`, Mermaid diagrams.

[Unreleased]: https://github.com/mlcousek/claude_trayapp/compare/0c88ac1...HEAD
