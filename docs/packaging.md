# Packaging: code signing and winget

Two things stand between a release and a stranger installing it without friction: the executable is unsigned, so
SmartScreen warns on first run, and the package is not in winget, so installing means downloading a zip by hand.
Both need an account only the repository owner can create, so this document is the procedure rather than the result.

## Code signing with SignPath

The release exe is unsigned today. SmartScreen shows a "Windows protected your PC" prompt the first time someone
runs it, which is the single biggest reason a stranger gives up on an unknown tool. SignPath's Foundation tier
signs open-source projects for free.

### What only you can do

1. Apply at <https://signpath.org/apply> with this repository. Approval is a human review and takes a few days.
   Say plainly that this is an unofficial community tool that reads a local Claude Code token and calls one
   Anthropic endpoint; that description matches the README and avoids questions later.
2. In the SignPath organisation you receive, create:
   - a **project** whose slug matches this repository, for example `claude-usage-tray`;
   - an **artifact configuration** for a zip holding a single exe (SignPath's "zip file" template signs the nested
     `ClaudeTrayApp.exe`);
   - a **signing policy** named `release-signing`, wired to the GitHub Actions trusted build system for this repo.
3. Add the repository secret `SIGNPATH_API_TOKEN` and the repository variable `SIGNPATH_ORGANIZATION_ID`.

### The workflow change, once you have those

Paste this into `.github/workflows/release.yml` between "Publish win-arm64" and "Package zips", so the exes are
signed before they are zipped and hashed. Pin `signpath/github-action-submit-signing-request` to a commit SHA the
way every other action in this repository is pinned; look the current one up when you add it rather than trusting a
SHA written here months earlier.

```yaml
      - name: Upload unsigned binaries for signing
        if: env.SIGNPATH_API_TOKEN != ''
        id: unsigned
        uses: actions/upload-artifact@<pin-a-sha> # v7.0.1
        with:
          name: unsigned-${{ env.VERSION }}
          path: artifacts/publish/

      - name: Sign with SignPath
        if: env.SIGNPATH_API_TOKEN != ''
        uses: signpath/github-action-submit-signing-request@<pin-a-sha>
        with:
          api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
          organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
          project-slug: claude-usage-tray
          signing-policy-slug: release-signing
          artifact-configuration-slug: zip-exe
          github-artifact-id: ${{ steps.unsigned.outputs.artifact-id }}
          wait-for-completion: true
          output-artifact-directory: artifacts/publish
```

Add `SIGNPATH_API_TOKEN: ${{ secrets.SIGNPATH_API_TOKEN }}` to the job's `env:` block so the `if:` conditions can
see it. Both steps skip when the secret is absent, so the workflow keeps producing today's unsigned release until
signing is actually configured.

Afterwards, verify a release with `Get-AuthenticodeSignature .\ClaudeTrayApp.exe` and drop the SmartScreen note
from the README's install section.

## winget

`packaging/winget/<version>/` holds the three manifests winget expects, one folder per release. Submit the newest
only; older folders stay as a record of what was prepared, and winget-pkgs keeps its own history of accepted
versions. They describe a **portable zip**: winget extracts the self-contained exe, registers `claude-usage-tray`
as a command alias, and removes the folder on uninstall. Nothing is written to Program Files and no installer runs,
which matches how the app is distributed.

Take the checksums from the release's own `SHA256SUMS.txt` rather than retyping them: a wrong hash makes
`winget install` fail for everyone, and it is the one field nothing else would catch.

Validate any change locally before submitting:

```powershell
winget validate --manifest packaging\winget\0.1.2
```

### Status

0.1.2 was submitted on 2026-09-08: <https://github.com/microsoft/winget-pkgs/pull/431350>. Once it is merged the
package installs with `winget install mlcousek.ClaudeUsageTray`. Automated validation runs first, then a moderator
looks at it; expect a question about the name, since "Claude" is Anthropic's trademark and this is a third-party
tool. The answer is the disclaimer already in the locale manifest's description.

### Submitting

Submission is a pull request to `microsoft/winget-pkgs`. The easiest route is `wingetcreate`, which builds the
manifests, validates them and opens the PR in one step:

```powershell
winget install Microsoft.WingetCreate
wingetcreate submit --token <a GitHub token with public_repo> packaging\winget\0.1.2
```

For later releases, update an existing package instead of writing manifests by hand:

```powershell
wingetcreate update mlcousek.ClaudeUsageTray --version 0.2.0 --urls `
  https://github.com/mlcousek/claude_trayapp/releases/download/v0.2.0/ClaudeUsageTray-0.2.0-win-x64.zip `
  https://github.com/mlcousek/claude_trayapp/releases/download/v0.2.0/ClaudeUsageTray-0.2.0-win-arm64.zip `
  --submit --token <token>
```

`wingetcreate` downloads each URL and computes the checksums itself, so they cannot drift from the release.

### Two things to decide before you submit

- **Sign first if you can.** An unsigned installer is allowed, but a first submission from a new publisher gets
  more scrutiny, and the manifests do not change when signing is added. Waiting for the certificate means one
  review instead of two.
- **The publisher name is currently your GitHub handle**, not your legal name, and the package identifier is
  `mlcousek.ClaudeUsageTray`. Both are yours to change in the locale manifest; the identifier is effectively
  permanent once accepted, so decide before the first PR.

Expect moderators to ask about the name. The answer is in the description already: this is an unofficial community
tool, not affiliated with or endorsed by Anthropic, and it ships none of Anthropic's branding.
