# Claude Code instructions — VueOneMapper

This file is auto-loaded by every Claude Code session opened against this
repo. Use it to document repo-wide conventions and any setup steps a new
session needs to know about.

## Git auth (push to `easensoy/Mapper`)

The remote `origin` carries a Personal Access Token (PAT) embedded in its URL,
so `git push` works for any tool/session that operates on this clone — no
interactive auth, no SSH, no credential helper prompt.

If `git push` ever fails with a 401/403, the token has been rotated or the URL
has been reset to the plain form. To restore it, paste the current token into
the command in `.claude/auth.local.md` (gitignored — never committed) and run
it. Verify with:

```bash
git ls-remote origin main
```

A SHA in the output means auth works.

### Why the token lives in the URL and not in a credential helper

This machine's global `credential.helper` is `store` (plaintext at
`~/.git-credentials`), and its system-level helper is `manager` (Windows
Credential Manager). Embedding the PAT in the remote URL bypasses both,
so every local process — including any background or scripted Claude Code
session — pushes through the same path without surprises.

### Token rotation

Treat the embedded PAT as ephemeral. When you finish a working session that
involved exposing the token (paste in chat, screenshot, etc.):

1. Revoke at <https://github.com/settings/tokens>.
2. Generate a new fine-grained PAT with `Contents: Read & Write` on
   `easensoy/Mapper` only.
3. Paste the new token into `.claude/auth.local.md` and re-run the command
   there.

## Contribution-graph note (separate from auth)

`easensoy/Mapper` is a private repo. Commits made under your verified email
only show on your contribution graph if **Settings → Profile → Contributions
→ Include private contributions on my profile** is ticked. This is a one-time
toggle; pushes themselves do not need anything special once it's on. The
contribution-counter indexer can lag several minutes after a burst of pushes.

## Build / test

- `MapperUI/` is the Windows Forms entry point — run `dotnet build` then
  launch `bin/Debug/net10.0-windows/MapperUI.exe`.
- `MapperTests/` carries the xUnit suite — `dotnet test`.

## Authoritative inputs you should not regenerate

- `MapperUI/MapperUI/Input/VueOne_IEC61499_Mapping.xlsx` — hand-crafted, three
  sheets with per-CAT-type rules. Master copy lives on the user's Desktop.
- `MapperUI/MapperUI/Input/SMC_Rig_IO_Bindings.xlsx` — physical IO bindings
  consumed by `IoBindingsLoader`.

## Commit conventions

- One file per commit, one push per commit.
- Imperative-mood subject line, focused on the *what*+*why*.
- Never include Claude attribution (no Co-Authored-By, no "Generated with"
  lines, nothing Claude-related).
