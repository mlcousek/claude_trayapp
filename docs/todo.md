# To do

Open work, most blocking first. Everything through v0.1.2 is done: the app is released, `main` is green, and
nothing on this list is required for it to keep working.

## Waiting on you

### Sign the winget CLA

The winget submission is open at <https://github.com/microsoft/winget-pkgs/pull/431350> and cannot merge until the
Contributor License Agreement is signed. Automated validation passed its first four checks before stopping here.

Comment on that pull request with exactly:

```
@microsoft-github-policy-service agree
```

It has to come from you: it is a legal agreement about intellectual property, made in your name. Afterwards the
remaining checks run on their own and a moderator reviews it, because it is a new package. Do not leave it open
indefinitely, since winget-pkgs eventually closes pull requests that go quiet.

Once merged: `winget install mlcousek.ClaudeUsageTray`.

If a moderator asks about the name, the answer is already in the package description: an unofficial third-party
tool, not affiliated with or endorsed by Anthropic, shipping none of their branding.

### Code signing with SignPath

The released exe is unsigned, so SmartScreen warns the first time anyone runs it. That is the largest remaining
barrier for someone who does not already trust the project. SignPath's Foundation tier signs open-source projects
for free. Approval is a human review taking a few days, so starting it early costs nothing and it does not block
the winget submission.

The repository was measured against their published conditions on 2026-09-08 and meets all of them; the two gaps
found, missing uninstall instructions and a missing code signing policy, are now in the README. `docs/packaging.md`
has the comparison table, the wording to put in the application, and the rest of the procedure.

What is left is yours, because the form sits behind a captcha and creates an account:

1. Confirm multi-factor authentication is on for your GitHub account, which they require of every team member.
2. Apply at <https://signpath.org/apply>, using the wording in `docs/packaging.md`.
3. On approval, create the project, artifact configuration and signing policy, add the two repository secrets, and
   paste the guarded workflow block into `.github/workflows/release.yml`.
4. Move the README's code signing policy into the present tense and drop the SmartScreen sentence. The next tag
   then produces a signed release.

## Deferred from the code review of 2026-09-08

None of these are urgent. Each was left deliberately rather than missed.

### The daily chart's DST bucketing

`AnalyticsCalculator.Daily` passes a single UTC offset to `SqliteStore.DailyTotals`, which buckets a range of up to
thirty days by local day. Rows near a daylight-saving transition land in the wrong day. Today's totals were fixed;
this one touches well-tested SQL aggregation, so it was left rather than risked. Doing it properly means bucketing
per row in .NET after a UTC-ranged query.

### Hot reload for a custom pricing file

`SettingsStore` watches `settings.json`, but `PricingProvider` reloads only when the configured path changes, not
when the contents of that file do. A `FileSystemWatcher` on the override path would make a hand-edited price sheet
behave like every other setting. Related: `PricingProvider.Reload` decides whether anything changed from the path,
model count and effective date, so an in-place price edit updates the table without raising `Changed`.

### A registry abstraction, so the last untested pieces can be tested

`ThemeManager` reads the Windows theme from the registry and `AutostartManager` writes the Run entry, so neither can
be covered without touching the real machine; the tests deliberately exercise only their pure helpers today. A thin
interface over those reads and writes would close that.

`SettingsCoordinator` is untested for the same practical reasons, and it holds real logic worth pinning: pricing
reloads only when the path differs, history prunes only when retention changed, the theme reapplies only when the
resolved override differs.

## Ideas, not committed to

### Use the endpoint's own severity instead of invented thresholds

The usage response carries a `limits[]` array with Anthropic's own `severity` per limit, and a probe showed it
reporting `warning` at 86 % where this app still calls 90 % the critical point. Parsing it would make the tray
colour agree with what Claude Code itself shows. It is unparsed today because the mapping from `limits[].kind` to
window keys was never verified against enough accounts to trust it.

### Cross-platform

WPF pins this to Windows. `ClaudeTrayApp.Core` carries no UI references, so an Avalonia front end would reuse the
providers, analytics, settings and history unchanged. It is a real project rather than an afternoon, and worth
starting only if people ask for it.

### Historical reporting

The charts answer "what is happening now". They do not answer "what did last month cost". The history database
already holds the data.
