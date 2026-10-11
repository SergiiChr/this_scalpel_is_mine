---
name: review-key-frames
description: Run the full regression with key frames, review only the key frames that changed against the committed baseline, and commit the reviewed ones as the new baseline. Use it for the full regression step before a PR and whenever a change needs its key frames reviewed (Visual verification in CLAUDE.md), instead of opening screenshots one by one.
---

# Review key frames

1. Run `./build.py screenshot_review` (smoke and lint pass first, see CLAUDE.md). It runs the same full regression as
   `./build.py test --all --with-key-frames`, then writes `build/review/report.md`. A non-zero exit means the
   regression failed: fix that first, the report is still written for looking into it.
2. Read `build/review/report.md`. It lists, per test folder, only key frames that are missing, changed or new against
   the baseline committed in `tests/KeyFrames`, with where in the view a change is. A missing key frame or view is
   always a finding.
3. Open every sheet it lists. A sheet is one key frame with its views side by side (top | oblique). Check it against
   the "Should show" line above it (the test's own description) and what the report's "Look for" line names. Open a
   `before` sheet only when a change needs explaining.
4. A changed key frame the change didn't mean to touch is a regression: find out why before handing off.
5. Once the regression passed and every listed sheet looks right, run `./build.py generate_baseline --no-run` and
   commit `tests/KeyFrames` with the change. `--no-run` takes the key frames just reviewed; after any later change,
   review again first. Skip this step when the report lists nothing.
6. In the handoff, list the sheets you reviewed and what you found.

## What goes into the baseline

- Only key frames of a full regression that passed. `./build.py generate_baseline` refuses anything else; don't work
  around it by copying files into `tests/KeyFrames`.
- Nothing from a failed case. A case that can't be fixed in this change is marked broken (CLAUDE.md): it then takes no
  key frames and `generate_baseline` drops its old ones.
- Nothing that shows a defect, even when the regression passed. Fix it and run the review again.
- If the report says there is no baseline, or nearly every key frame shows as changed, the comparison itself is off
  (no baseline committed yet, another renderer). Stop and report that. Don't generate a baseline unless that is the
  task, and don't skip the review of the key frames your change touches.
- After rebasing onto a main with other key frames, take main's `tests/KeyFrames`, then review and generate again.

`screenshot_review --no-run` rebuilds the report from the last run's key frames. `--all` also makes sheets for unchanged key frames, for
a review from scratch.
