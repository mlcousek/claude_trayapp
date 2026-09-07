> **Unofficial tool.** Claude Usage Tray is a community project. It is not affiliated with, endorsed by, or supported by Anthropic. It relies on an undocumented endpoint that can change or stop working at any time.

## Downloads

| File | For |
|---|---|
| `ClaudeUsageTray-{{VERSION}}-win-x64.zip` | Windows 11 on x64 |
| `ClaudeUsageTray-{{VERSION}}-win-arm64.zip` | Windows 11 on Arm64 |

Unzip anywhere and run `ClaudeTrayApp.exe`: no installer, no .NET runtime to install, no admin prompt. The executable is not code-signed, so SmartScreen may ask once (More info, Run anyway). Claude Code must be installed and signed in; the app reads its token and never writes to `~/.claude`.

## SHA256 checksums

```text
{{SHA256SUMS}}
```

Verify before unzipping with `Get-FileHash .\ClaudeUsageTray-{{VERSION}}-win-x64.zip` in PowerShell or `certutil -hashfile ClaudeUsageTray-{{VERSION}}-win-x64.zip SHA256` in Command Prompt, and compare with the line above (`SHA256SUMS.txt` is attached as well).

## Changes in {{VERSION}}

{{CHANGELOG}}

{{GENERATED_NOTES}}
