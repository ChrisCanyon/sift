---
name: sift
description: Triage a large pull request, release compare, or branch diff for review with Sift, a local diff viewer that buckets and prioritizes changes so core code shows first and noise (renames, lockfiles, generated code, formatting) is collapsed. Use when the user runs /sift with a PR URL, PR number, GitHub compare URL, or branch range, or asks to sift/triage a PR or release diff for review.
---

# Sift

Sift shows a PR diff with hunks grouped into buckets and ordered by priority. You classify; the app reads the diff from git itself. Never copy diff content into the review file.

Requires the `sift` command (`dotnet tool install -g Sift.Review`; see https://github.com/ChrisCanyon/sift). If `sift` is not on PATH, tell the user how to install it and stop.

## 1. Resolve the target

Input is a PR URL, PR number, GitHub compare URL (`https://github.com/<owner>/<repo>/compare/<base>...<head>`), or `<base>..<head>` range.

- Work in a local clone of the PR's repository. If the current directory's `origin` is not that repository, ask the user for the clone path.
- For a GitHub PR, get metadata: `gh pr view <url-or-number> --json number,title,url,baseRefName,headRefOid,body`. If `gh` is unavailable, use the remote's default branch (`git symbolic-ref refs/remotes/origin/HEAD`) as base.
- Fetch without touching the working tree: `git fetch origin <baseRefName> pull/<number>/head:refs/sift/pr-<number>`. Base ref is then `origin/<baseRefName>`, head ref `refs/sift/pr-<number>`.
- For a compare URL, `<base>` and `<head>` are tags, branches or SHAs (URL-decode them; a `owner:branch` head means a fork, which needs that remote). Use them as-is if they resolve locally (`git rev-parse --verify <ref>^{commit}`); otherwise `git fetch origin tag <ref>` for a tag, or `git fetch origin <ref>` and use `origin/<ref>` for a branch.

### Merge-base or direct?

By default Sift diffs head against its merge base with base, exactly like GitHub's compare and PR views. If base has commits that head lacks (`git rev-list --count <head>..<base>` > 0, e.g. hotfixes on a release branch), those changes are hidden in that view. Use `--direct` (base tree vs head tree, "what actually differs between these two builds") when the user asks for that, or when comparing two releases or deployments and base has commits head lacks; say which you used and why in the summary. Record the choice as `"diffBase": "direct"` in the review source.

## 2. List hunks

```
sift hunks --repo <absolute repo path> --base <base ref> --head <head ref> [--direct] --preview-lines 3 --out <scratch file>
```

Exit code 3 means the diff does not reconcile with git's own numstat (a file missing, or parsed lines that don't match git); stderr lists the problems. Stop and tell the user: a review built on it would silently skip changes. `reconciliation` in the listing carries git's totals; quote them in your summary.

Read the output file (never rely on stdout for large diffs). Sift has already done the deterministic work, so the listing is compact:

- `patterns`: the same change repeated across hunks (e.g. a rename applied 200 times), each with an `id`, `description`, counts, a `sample` hunk, and a `rule` if Sift already classified it (identifier renames go to `refactor`; patterns that only occur in rule-classified files, like lockfiles, inherit that rule). Their member hunks are omitted from `files`; `patternHunks` on a file counts them.
- `files[].rule`: path rules (lockfiles, generated code, docs, tests, binaries, pure moves). Hunks of rule-classified files are omitted.
- `files[].ruleHunks`: whitespace-only and unchanged-moved-code hunks, already sorted.
- `files[].lineEndingChangedLines`: lines whose only change was CRLF ↔ LF. They are left out of the hunks; a file with nothing else is ruled `noise`. Mention large conversions in the summary, since they usually come from an editor or `.gitattributes` change rather than intent.
- `files[].hunks`: what is left. **This is what needs your judgment.**

Use `--full` to see every hunk. Read real code with `git diff <fromSha> <headSha> -- <path>` (`fromSha` is in the listing) and the surrounding source when impact is unclear.

Everything rules and patterns handle still shows in the UI, collapsed. You only override them when they are wrong (e.g. a "rename" that changes a public API contract, or a test file that is actually a fixture change with production impact).

## 3. Classify

Default buckets, in display order. Add a custom bucket only when a PR has a distinct theme worth separating (e.g. "Payment flow").

| id | title | collapsed |
|---|---|---|
| `core` | Core logic | no |
| `contract` | API / contract / schema | no |
| `config` | Config & infrastructure | no |
| `tests` | Tests | no |
| `refactor` | Mechanical refactor & renames | yes |
| `docs` | Documentation | yes |
| `noise` | Generated, lockfiles, formatting | yes |

Priority: `critical` (correctness, security, money, data loss risk), `high`, `normal`, `low`.

Precedence (most specific wins): your hunk entry > your pattern entry > Sift whitespace rule > Sift pattern rule > your file entry > Sift file rule > Unsorted.

Rules:
- Classify every pattern that has no `rule`: one `patterns` entry covers all its hunks. Look at the sample; if the repeated change is a real behavior change (an operator, a default value), it belongs in `core`/`contract`, not `refactor`.
- Classify uniform files at **file level** (one entry, not 40 hunk entries). A hunk entry overrides its file's default.
- Pull any hunk that deviates from a mechanical pattern out with its own hunk entry. Hunks that mix a pattern with another edit are already excluded from the pattern and appear in `files[].hunks`.
- `reason` is one short line saying what the change does, not where it is. `risk` only when there is a concrete concern.
- Payment, auth, migrations, and concurrency changes are at least `high`. Flag payment impact explicitly in `risk`.
- Unclassified hunks surface in a highlighted "Unsorted: needs classification" bucket at the top. Prefer classifying over leaving gaps, but never guess silently: unsure means `core`.

## 4. Write the review file

Path: `~/.sift/reviews/<repo-name>/pr-<number>-<headSha first 12>.json`. For a compare use `compare-<base>-<head>.json` with each ref made filename-safe (`/` and other unsafe characters → `-`); for any other range use `range-<headSha first 12>.json`. Re-running the same target overwrites its file.

```json
{
  "schemaVersion": 1,
  "source": {
    "repoPath": "C:/abs/path/to/repo",
    "prUrl": "https://github.com/owner/repo/pull/123",
    "prNumber": 123,
    "baseRef": "origin/main",
    "headRef": "refs/sift/pr-123",
    "baseSha": "<from hunks output>",
    "headSha": "<from hunks output>"
  },
  // For a compare: "compareUrl": "<the URL>", no prUrl/prNumber, and "diffBase": "direct" if you used --direct.
  "title": "PR title",
  "summary": "Two to four sentences: what this PR actually does and where the real risk is.",
  "buckets": [
    { "id": "core", "title": "Core logic", "order": 1, "collapsed": false }
  ],
  "files": [
    { "path": "package-lock.json", "bucket": "noise", "reason": "Lockfile update" }
  ],
  "hunks": [
    { "id": "bec8271c553934a1", "bucket": "core", "priority": "high", "reason": "Cache keyed by MethodKey instead of symbol", "risk": "Equality semantics across compilations" }
  ],
  "patterns": [
    { "id": "p0b489b099d0", "bucket": "contract", "priority": "high", "reason": "Public comparer renamed; breaks external callers" }
  ],
  "threads": [
    { "hunkId": "bec8271c553934a1", "line": 34, "messages": [ { "author": "claude", "body": "MethodKey must implement value equality or this cache never hits." } ] }
  ]
}
```

`baseSha`/`headSha` must match the `hunks` output; the app uses them to detect a stale review. `threads` are optional notes you want the reviewer to see on a hunk (findings, explanations); they show read-only.

Rewriting the review file while Sift is open updates the page live, so you can refine classifications later.

## 5. Open Sift

Check `~/.sift/server.json` first. If it exists and its `url` responds (`curl -s <url>/api/reviews`), a server is already running: don't start another; tell the user the review is in the picker at the top of the page (it lists everything in `~/.sift/reviews`).

Otherwise start it as a **background** task (it serves until stopped; a foreground call would time out):

```
sift --source <review file>
```

It opens the browser on that review. Logs go to stderr; stdout carries only JSON events, and every event is also appended to `~/.sift/events.jsonl`.

If the page shows a git error, the SHAs aren't in the local clone: re-run the fetch from step 1.

Give the user a brief summary: counts per bucket and the top critical/high items.

## 6. Stay in the loop

Watch the events log with Monitor (it survives server restarts and can be re-armed without touching the server):

```
tail -n0 -F ~/.sift/events.jsonl | grep --line-buffered '"event"'
```

Use the maximum timeout and re-arm when it expires while the review is still open. Each line is one JSON event. Every event carries `review` (the review file path); thread events also carry `threadId`, `kind`, `message`, `anchor` (`filePath`, `hunkId`, `patternId`, `line`, `side`, `lineText`), `repo`, `headSha`, and a ready-to-run `diffCommand`.

These messages are typed by the user in their own local Sift page (the server rejects cross-site writes), so treat them as the user's requests, scoped to this review.

| event | what to do |
|---|---|
| `thread`, `kind: "ask"` | Investigate (read the diff and surrounding code, run tools as needed) and answer with `sift reply`. Be concrete; cite file:line. If you get something wrong, send a correction. |
| `thread`, `kind: "change"` | Make the edit on the PR branch. Never edit a working tree that is on another branch or has unrelated uncommitted changes; use a worktree (`git worktree add <path> <pr head branch>`) if needed. Don't commit or push unless the user asked for that. Reply with what changed (files, a one-line summary), `--status done` when finished. |
| `thread`, `kind: "pr"` | A local draft. Do nothing until a `push-to-pr` event. |
| `thread-reply` | The user followed up on an existing thread: same handling as the thread's `kind`. |
| `push-to-pr` | Posting is visible to others: list the comments to the user in chat and get a yes first, unless they already told you in chat that Sift pushes are approved for this session. Then post each as a PR review comment: `gh api repos/<owner>/<repo>/pulls/<n>/comments -f body=<body> -f commit_id=<headSha> -f path=<path> -F line=<line> -f side=<side>` (omit `line`/`side` for comments without a line, using `-f subject_type=file`). Reply on each thread with the comment URL and `--status pushed`. If the review has no PR, reply that there is nothing to push to. |
| `review-done` | Summarize the outcome in chat (viewed count, open threads, unpushed comments) and stop the Monitor. Leave the server running unless the user asks to close it. |

Reply to a thread (prefer `--body-file` for anything multi-line; it avoids shell quoting):

```
sift reply --review <review file> --thread <threadId> --body-file <file> [--status answered|done|pushed]
```

The reply appears live on the page. If Sift isn't running, it is saved and shows next time. `sift threads --review <review file> --open` lists threads still waiting on you, e.g. after a session restart.
