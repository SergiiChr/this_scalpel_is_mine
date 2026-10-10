---
name: review-key-frames
description: Run the full regression with key frames and review only the key frames that changed, from one report. Use it for the full regression step before a PR and whenever a change needs its key frames reviewed (Visual verification in CLAUDE.md), instead of opening screenshots one by one.
---

# Review key frames

1. Run `./build.py review` (smoke and lint pass first, see CLAUDE.md). It runs the same full regression as
   `./build.py test --all --with-key-frames`, then writes `build/review/report.md`. A non-zero exit means the
   regression failed: fix that first, the report is still written for looking into it.
   The first run on a new base also renders the base commit's key frames in a git worktree (cached in
   `build/key-frames/<commit>`), so it takes about twice as long.
2. Read `build/review/report.md`. It lists, per test folder, only key frames that are new or changed against the
   commit where this branch left `origin/main`, with where in the view the change is.
3. Open every sheet it lists. A sheet is one key frame with its views side by side (top | oblique). Check what the
   report's "Look for" line names. Open a `before` sheet only when a change needs explaining.
4. A changed key frame the change didn't mean to touch is a regression: find out why before handing off.
5. In the handoff, list the sheets you reviewed and what you found.

`--no-run` rebuilds the report from the last run's key frames. `--base REF` compares against another ref. `--all`
also makes sheets for unchanged key frames, for a review from scratch.
