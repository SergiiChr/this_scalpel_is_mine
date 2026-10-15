# Development and release

Fast changes land on `develop`; a slower, independent visual verification gates what reaches `main`.
Commands and test policy: [CLAUDE.md](../CLAUDE.md).

## Branches

- `main`: released state. Only release PRs (`release/<hash>` → `main`) land here, merged with a merge commit.
- `develop`: integration branch. Feature PRs land here, squashed.
- `<feature>`: branched from `develop`, one change each.
- `release/<hash>`: cut from the `develop` commit that passed visual verification. It adds only the new key frame
  baseline.

Only release branches commit `tests/KeyFrames`, so feature PRs never conflict on key frames.

## Development

1. Branch from `develop`.
2. Make the change. Run `./build.py lint` and whatever else the change needs for self-review.
3. Run the affected and newly added tests (`./build.py test --tag ... --case ...`).
4. Run `./build.py test` (smoke) or `--all` (full regression) locally only for a wide core change that can break
   unrelated modules. Otherwise CI covers them.
5. For a user-visible change, take the key frames of its suites and check them against the baseline:
   `./build.py test --tag ... --with-key-frames`, then `./build.py screenshot_review --focused`. Review the sheets in
   `build/review/report.md` (see the `review-key-frames` skill). Don't commit them to `tests/KeyFrames`.
6. Raise a PR to `develop` (see PR description).
7. Review the PR without bias: a subagent with no context of the work gets only the diff and the PR description.
   Address everything it finds.
8. Hand over a summary of the change and its evidence, with the reviewed sheets for a visual change, as a prompt
   that asks for the merge decision.
9. Watch the PR for merge conflicts and failing CI until it's merged: merge `develop` into the branch to resolve
   conflicts, never rebase a branch with an open PR.

## PR description

- Bullet points with a high level summary of the changes.
- Caveats and unexpected results.
- Testing only when specific testing was the point (performance of X improved from Y ms to Z ms).
- Nothing unrelated, no signatures.

## Verification and release

A scheduled run verifies `develop`:

1. Take the newest `develop` commit whose CI passed and note its hash. Skip when it was already verified, a release
   PR is open, or a fix or decision from an earlier run is still open.
2. Run `./build.py screenshot_review` on that commit.
3. Review the listed sheets against the PRs merged into `develop` since the last release.
4. A clear-cut defect gets a fix PR to `develop` under the development flow above. A change whose intent isn't
   obvious (user flow, behavior) goes to the owner for a decision. Either way the run stops; the next one verifies
   again.
5. With no issues: branch `release/<short hash>` from that commit, run
   `./build.py generate_baseline --use-latest-set` and commit `tests/KeyFrames` when anything changed, and open a PR
   to `main`. Its description lists the verified hash, every PR merged since the last release (titles, linked), and
   how many key frames changed and why, per PR.
6. Merge the release PR with a merge commit, never squash: a squashed release leaves `develop` without that commit,
   and the next sync conflicts on everything the release touched.

## Keeping develop in sync

After a release PR merges, merge `main` into `develop` and push. It's a fast-forward unless `develop` moved on; then
it's a merge commit that brings over only the baseline. Never rebase or force-push `develop`: open feature branches
are built on its history.
