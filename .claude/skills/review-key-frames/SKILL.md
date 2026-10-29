---
name: review-key-frames
description: >-
  Review key frames changed against the committed baseline and commit accepted frames.
  Use for focused feature runs or full release regressions whenever key frames need review
  (Visual verification in CLAUDE.md and docs/RELEASE.md), instead of opening screenshots one by one.
---

# Review key frames

A feature branch reviews only its own suites: `./build.py test --tag ... --with-key-frames`, then
`./build.py screenshot_review --focused`, which fails on missing key frames or a missing baseline.
The focused test run must pass separately; a successful comparison does not certify the run. Steps 2 to 6 follow, with
`./build.py generate_baseline --focused` in step 5: it adds or replaces the run's key frames and deletes none.
Release verification follows [docs/RELEASE.md](../../../docs/RELEASE.md) and runs every step.

1. Run `./build.py screenshot_review` on a commit whose CI passed. It runs the same full regression as
   `./build.py test --all --with-key-frames`, then writes `build/review/report.md`. The regression stops at its first
   failed suite. It exits non-zero when the regression failed (the report is still written, the suites it didn't run
   showing as missing), when no baseline exists, and while any key frame differs from the baseline.
2. Read `build/review/report.md`. It lists, per test folder, only key frames that are missing, changed or new against
   the baseline committed in `tests/KeyFrames`, with where in the view a change is. A missing key frame or view is
   always a finding.
3. Open every sheet it lists. A sheet is one key frame with its views side by side (top | oblique). Check it against
   the "Should show" line above it (the test's own description) and what the report's "Look for" line names. Open a
   `before` sheet only when a change needs explaining.
4. A changed key frame the change didn't mean to touch is a regression: find out why before handing off.
5. Once every listed sheet looks right, run
   `./build.py generate_baseline --use-latest-set` (`--focused` on a feature branch) and commit `tests/KeyFrames`.
   It writes only the listed key frames; the others keep their files. `--use-latest-set` takes the key frames just
   reviewed; after any later change, review again first. Skip this step when the report lists nothing.
6. In the handoff, list the sheets you reviewed and what you found.

## What goes into the baseline

- Only key frames of a full regression that passed, or of a passing focused run accepted by
  `generate_baseline --focused`. `generate_baseline` refuses anything else; don't work around it by copying files into
  `tests/KeyFrames`. A case that can't be fixed in this change follows the known-issue or broken policy in
  [CLAUDE.md](../../../CLAUDE.md#test-structure-and-policy).
  Broken cases are skipped and take no key frames; a passing full regression drops their old baseline frames.
- Nothing that shows a defect, even when the regression passed. Fix it and run the review again.
- If the review fails for lack of a baseline, or nearly every key frame shows as changed, the comparison itself is
  off (no baseline committed yet, another renderer: a machine without a display draws in software, not on the GPU the
  baseline was drawn on). Stop and report that. Don't generate a baseline unless that is
  the task, and don't skip the review of the key frames your change touches.
- On a merge conflict in `tests/KeyFrames`, take `develop`'s files, then take the key frames and generate again.

`screenshot_review --use-latest-set` rebuilds the report from the last run's key frames. `--all` also makes sheets
for unchanged key frames, for a review from scratch.
