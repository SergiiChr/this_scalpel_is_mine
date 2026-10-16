# Development and release

Fast changes land on `develop`; a commit of it goes through a slower, independent visual verification before `main`
fast-forwards to it.
Commands and test policy: [CLAUDE.md](../CLAUDE.md).

## Branches

History is linear: no merge commits on any branch.

- `main`: released state. It only fast-forwards to a `develop` commit that passed release verification, so it's
  always an ancestor of `develop` and never needs syncing back.
- `develop`: integration branch. Every PR lands here squashed: features, release fixes and release key frames.
- `<feature>`: branched from `develop`, one change each.
- `release/<short hash>`: points at the verified `develop` commit, only to carry the release PR to `main`.
- `keyframes/<short hash>`: the release review's accepted key frames on top of the verified commit, PR'd to `develop`.

`tests/KeyFrames` is `develop`'s baseline. A feature commits the key frames of the suites it ran, so the release review
lists only what changed beyond them: side effects on suites the feature didn't run. The key frames a release review
accepts reach `main` with the next release.

## Development

1. Branch from `develop`.
2. Make the change. Run `./build.py lint` and whatever else the change needs for self-review.
3. Run the affected and newly added tests (`./build.py test --tag ... --case ...`).
4. Run `./build.py test` (smoke) or `--all` (full regression) locally only for a wide core change that can break
   unrelated modules. Otherwise CI covers them.
5. For a user-visible change, take the key frames of its suites and check them against the baseline:
   `./build.py test --tag ... --with-key-frames`, then `./build.py screenshot_review --focused`. Review the sheets in
   `build/review/report.md` (see the `review-key-frames` skill). It fails only on missing key frames: changed and new
   ones are the evidence for the handoff. Once they look right, `./build.py generate_baseline --focused` adds or
   replaces them in `tests/KeyFrames`; commit it with the change. It never deletes: removed key frames go at release.
6. Raise a PR to `develop` (see PR description).
7. Review the PR without bias: a subagent with no context of the work gets only the diff and the PR description.
   Address everything it finds.
8. Hand over a summary of the change and its evidence, with the reviewed sheets for a visual change, as a prompt
   that asks for the merge decision.
9. Watch the PR for merge conflicts and failing CI until it's merged: merge `develop` into the branch to resolve
   conflicts, never rebase a branch with an open PR. On a conflict in `tests/KeyFrames`, take `develop`'s files, run
   the focused key frames again and regenerate them.

## PR description

- Bullet points with a high level summary of the changes.
- Caveats and unexpected results.
- Testing only when specific testing was the point (performance of X improved from Y ms to Z ms).
- Nothing unrelated, no signatures.

## Verification and release

A scheduled run picks a `develop` commit and verifies it. `develop` keeps taking feature PRs the whole time. Fixes for
what the verification finds land on `develop` like any other PR, and the next run verifies a newer commit that
contains them. One release PR is open at a time.

1. Pick. Take the newest `develop` commit whose CI passed. Skip when `main` already contains it.
2. Verify. Run `./build.py screenshot_review` on that commit. A suite that still fails after its retry is an issue
   (step 4), even when CI passed.
3. Review the listed sheets against the PRs merged into `develop` between `main` and that commit. The features
   committed their own key frames, so every listed one is unexpected: a side effect, a missing key frame or a flaky
   render.
4. Each problem gets an issue, and the run that opens it works it; it never opens an issue and stops. A clear-cut
   defect gets its real cause fixed in a PR to `develop`, under the development flow above, driven to green CI and
   handed to the owner to merge. A change whose intent isn't obvious (user flow, behavior) goes to the owner for a
   decision, with a recommendation, and the run acts on the answer. Once the issues are closed, the next run starts
   over at step 1.
5. With no issues: push `release/<short hash>` at the verified commit and open a PR from it to `main`. Its
   description lists the commit, every PR merged into `develop` since the last release (titles, linked), and how many
   key frames the features changed and the release review accepted, and why. When the review listed key frames, run
   `./build.py generate_baseline --use-latest-set`, commit `tests/KeyFrames` on `keyframes/<short hash>` from the
   verified commit and open a PR to `develop`. On a conflict in `tests/KeyFrames`, a feature merged since owns the
   file: take `develop`'s.
6. The owner approves the release PR. Release by fast-forwarding `main` to the verified commit
   (`git push origin <hash>:main`); GitHub then marks the PR merged. Never merge it with the button: every method
   there makes a new commit, and `main` would stop being an ancestor of `develop`.

The key frames PR is merged as soon as its CI is green, without waiting for the owner.
Never rebase or force-push `develop` or `main`: open feature branches are built on `develop`'s history.

Repository settings: squash is the only merge method. `main` and `develop` require linear history and block force
pushes. `develop` takes changes only through PRs whose CI passed. `main` takes only fast-forward pushes from the owner
(a bypass of its PR rule), of commits whose CI passed on `develop`.
