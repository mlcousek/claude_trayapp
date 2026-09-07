Claude Usage Tray
=================

An unofficial Windows 11 system-tray app that shows your Claude usage at a glance:
a live percentage in the tray icon and a flyout with every usage window, reset
countdowns, plan tier, burn rate, history charts and local token/cost analytics.

Not affiliated with, endorsed by, or supported by Anthropic. It relies on an
undocumented endpoint that can change or stop working at any time.

Run
---
1. Keep ClaudeTrayApp.exe and pricing.json together in any folder you like.
2. Run ClaudeTrayApp.exe. No installer, no .NET runtime to install, no admin prompt.
   The executable is not code-signed; if SmartScreen asks, choose "More info",
   then "Run anyway".
3. Claude Code must be installed and signed in at least once: the app reads the
   token Claude Code stores locally and never writes to it. If the token has
   expired, run any Claude Code command to refresh it.
4. Optional: right-click the tray icon and choose "Start with Windows".

Files
-----
ClaudeTrayApp.exe   the app, self-contained single file
pricing.json        API-equivalent prices for the local cost estimate; editable
LICENSE.txt         MIT licence
README.txt          this file

Privacy
-------
No telemetry. The only network destination is api.anthropic.com. The app writes
only to %LOCALAPPDATA%\ClaudeTrayApp (cache, history, logs; tokens redacted) and
%APPDATA%\ClaudeTrayApp\settings.json.

Source, documentation and issues: https://github.com/mlcousek/claude_trayapp
Releases and SHA256 checksums:    https://github.com/mlcousek/claude_trayapp/releases
