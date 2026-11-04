# Sift

Local PR review for large AI-generated diffs. Core changes come first; renames, lockfiles, generated code and formatting are grouped and collapsed. Discuss any hunk with Claude from the page.

## Install

Needs git, the .NET 9 SDK and Node 20+ (to build the UI). The skill uses [`gh`](https://cli.github.com/) for PR metadata and posting comments.

```powershell
git clone https://github.com/ChrisCanyon/sift
cd sift
./install.ps1            # -LinkSkill to link the skill to this clone instead of copying it
```

This installs the `sift` command as a global .NET tool and the `/sift` skill into `~/.claude/skills/sift`. Without PowerShell:

```bash
dotnet pack src/Sift -c Release -o artifacts
dotnet tool uninstall -g Sift.Review; dotnet tool install -g Sift.Review --add-source artifacts
cp -r skills/sift ~/.claude/skills/
```

## Use

In Claude Code:

```
/sift https://github.com/owner/repo/pull/123
```

The skill fetches the PR, classifies it, writes `~/.sift/reviews/<repo>/pr-<n>-<sha>.json`, opens Sift and stays connected to answer questions, make requested changes and post PR comments.

Directly:

```
sift --source <review.json>     # open a review (no --source: pick from saved reviews)
sift hunks --repo <path> --base <ref> [--head <ref>] [--full] [--out <file>]
sift reply --review <review.json> --thread <id> --body-file <file> [--status answered|done|pushed]
sift threads --review <review.json> [--open]
```

From source: `dotnet run --project src/Sift -- <args>` (the UI builds on the first `dotnet build`).

## How it works

- **Diff:** read from plain git (`merge-base..head`); the review file only points at SHAs, never copies diff content.
- **Deterministic sorting:** path rules (lockfiles, generated, docs, tests, binaries, pure moves) and whitespace-only hunks. Repeated changes are grouped into patterns by token-level replacement (e.g. `Foo → Bar` across 200 files), so a hunk that does the rename *plus* something else stands out.
- **AI sorting:** Claude classifies what is left, per hunk, pattern or file, with a reason and a risk. The most specific classification wins; nothing is hidden silently (unclassified hunks land in Unsorted at the top).
- **Loop:** UI actions are JSON events on stdout and `~/.sift/events.jsonl`; `sift reply` answers appear live. Reviewer state lives next to the review in `<review>.state.json`.
- **Local only:** binds to 127.0.0.1, rejects foreign Host headers, and refuses cross-site writes so other pages can't send instructions to the agent.

Keyboard: `j`/`k` next/previous card, `v` toggle viewed, `o` expand/collapse. Click a line number to comment on that line.

`SIFT_HOME` overrides `~/.sift`.
