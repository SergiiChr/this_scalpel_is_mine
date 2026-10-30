# Development and release

Fast changes land on `develop`; a commit of it goes through a slower, independent visual verification before `main`
fast-forwards to it.
Commands and test policy: [CLAUDE.md](../CLAUDE.md).

## Branches

History on `main` and `develop` is linear: no merge commits.

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
2. Make the change. Run `./build.py lint` and whatever else the change needs.
3. Run the affected and newly added tests (`./build.py test --tag ... --case ...`). They check the frame budget too.
4. Run `./build.py test` (smoke) or `--all` (full regression) locally only for a wide core change that can break
   unrelated modules. Otherwise CI covers them. When suites fail, fix each and check the fix on that suite alone
   (`--tag`, `--case`); then the same command with `--resume` runs only the suites the last run didn't pass. Run it
   whole again only when a fix was itself a wide change.
5. For a user-visible change, take the key frames of its suites and check them against the baseline:
   `./build.py test --tag ... --with-key-frames`, then `./build.py screenshot_review --focused`. Review the sheets in
   `build/review/report.md` (follow [review-key-frames](../.claude/skills/review-key-frames/SKILL.md)). The comparison
   fails on missing key frames or a missing baseline; the test run must pass separately. Changed and new
   ones are the evidence for the handoff. Once they look right, `./build.py generate_baseline --focused` adds or
   replaces them in `tests/KeyFrames`; commit it with the change. It never deletes: removed key frames go at release.
6. Follow [self-review](../.claude/skills/self-review/SKILL.md), then rerun the checks above for what it changed.
7. Raise a PR to `develop` (see PR description).
8. Hand over a summary of the change and its evidence, with the self-review summary and, for a visual change, the
   reviewed sheets, as a prompt that asks for the merge decision.
9. Watch the PR for merge conflicts and failing CI until it's merged: merge `develop` into the branch to resolve
   conflicts, never rebase a branch with an open PR. On a conflict in `tests/KeyFrames`, take `develop`'s files, run
   the focused key frames again and regenerate them.

## PR description

See [PR description](../CLAUDE.md#pr-description) in CLAUDE.md.

## Verification and release

The owner starts a release run on demand; it picks a `develop` commit and verifies it.
`develop` keeps taking feature PRs the whole time. Fixes for what the verification finds land on `develop` like any
other PR, and the next run verifies a newer commit that contains them. One release PR is open at a time.

1. Pick. Take the newest `develop` commit whose CI passed. Skip while a release PR is open or `main` already contains
   it.
2. Verify. Run `./build.py screenshot_review` on that commit, on a machine with a Wayland desktop and an NVIDIA GPU: the
   baseline is drawn by one, and a cloud container's software renderer changes nearly every key frame. A failed suite
   is an issue (step 4), even when CI passed: the run stops there, so fix it and run again.
   Start it detached after `./build.py setup` and check on it until it ends, keeping the session alive:
   `setsid nohup sh -c './build.py screenshot_review; echo "exit $?"' > build/release.log 2>&1 &`.
   Run no other `./build.py test` in that checkout meanwhile: it would replace the run's key frames and logs.
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
6. Ask the owner to approve the release in the project thread (the PR is opened under their account, so GitHub won't
   take their review). On their yes, fast-forward `main` to the verified commit (`git push origin <hash>:main`);
   GitHub then marks the PR merged. Never merge it with the button: every method there makes a new commit, and
   `main` would stop being an ancestor of `develop`. Delete `release/<short hash>` and `keyframes/<short hash>` once
   their PRs are merged.

The key frames PR is merged as soon as its CI is green, without waiting for the owner.
Never rebase or force-push `develop` or `main`: open feature branches are built on `develop`'s history.

Repository settings: squash is the only merge method. `main` and `develop` require linear history and block force
pushes. `develop` takes changes only through PRs whose CI passed. `main` takes only fast-forward pushes, of commits whose CI
passed on `develop`: its PR rule has a bypass for whoever the release run pushes as (the owner, or the Claude GitHub
App).
