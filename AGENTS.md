# AGENTS.md

**The guidance for this repository lives in [`CLAUDE.md`](CLAUDE.md). Read that file - all of it - before
working here. It applies to every coding agent, not just Claude Code.**

This file is deliberately a pointer rather than a copy.

An earlier version of `AGENTS.md` was a find-and-replaced duplicate of `CLAUDE.md`, and it did what
duplicated instructions always do: it went stale. By the time it was noticed it was 66 lines behind, still
told its reader the platform was "at Milestone 1" with summaries, RAG and packaging "not built" (all three
had long since shipped), and the find-replace had corrupted a sentence about the **claude.ai** MCP
connector into one about a different product - turning a true statement into a false one. Anything worth
telling an agent is worth telling every agent, so there is one file and this points at it.

## If you are not Claude Code

`CLAUDE.md` is addressed to Claude Code, but essentially nothing in it is Claude-specific - it is
architecture, cross-boundary contracts, test strategy, and the release/PR rules. Read "Claude Code" as
"you". Two notes:

- Where it names a **Claude Code skill** (`superpowers:*`, `/code-review`, and the like), that is a
  Claude Code feature. Use your own equivalent, or do the work directly - the *requirement* it is
  attached to (write the failing test first, open a PR rather than merging locally) still applies to you.
- Where it says **"Claude"** as part of a product name - the **claude.ai MCP connector**, the OAuth 2.1
  client registration, `X-Worker-Secret` callbacks - that is Diariz functionality and must not be
  renamed. Do not repeat the find-replace that broke this file.

## The rules most often missed

Full detail is in `CLAUDE.md`; these are the ones worth naming twice.

- **TDD is required.** Write the failing test, watch it fail, then write the minimum to pass it.
- **Never commit or push to `main`, and never merge locally.** Every change lands through a PR that
  passes CI. A bug fix opens a GitHub issue first, and the PR body closes it with `Fixes #<n>`.
- **Every user-facing PR ships exactly one release**: bump `version.json` *and* its seven mirrors, and add
  one `RECENT[0]` entry to `apps/web/src/lib/releaseNotes/current.ts`. That is the only release-notes file
  an ordinary PR touches - the rest of `lib/releaseNotes/` is the epoch layer over the archive, and
  `archive.ts` must stay out of every eager module. A docs-only PR skips the bump - say so in the PR body.
- **Never put production data in the repo.** No real names, email addresses, company names, recording
  titles or transcript text - in code, comments, **test fixtures**, docs, commit messages, issues or PRs.
  This repository is public. Invent fixture names (`Ada`, `Grace`, `Alice`); report findings from a live
  query as counts and percentages, never as rows.
- **No em or en dashes in user-facing text.** A plain hyphen, in UI strings, i18n catalogues and release
  notes.
- **Never `git add -A` here.** It sweeps hundreds of untracked scratch files into the commit. Stage
  explicit paths.
