---
name: self-review
description: >-
  Review a finished branch before handing it off for merge in any repository: fix bugs with /code-review, clean up
  with /simplify, check the goal, and get an independent review from an agent without the task's context.
---

# Self-review

Find the base branch (the branch the work started from, otherwise the default branch).
Use `~/.claude/CODING.md` and the project's coding guide (such as CODING.md) as the code standard in every step, and
follow the project's agent and contributing docs (such as AGENTS.md, CLAUDE.md or CONTRIBUTING.md).

Whenever you hand reviewers the changes, here or through `/code-review` and `/simplify`, give them file line numbers
along with the diff, so they can cite `file:line` without looking each one up.
Write `git diff <base>` to a file in the scratchpad, and next to it the changed lines of the working tree from
`git diff -U0 <base> | grep -E '^(\+\+\+|@@)'`, where each hunk's `+start,count` gives the current file's lines.

## 1. Bugs

Run `/code-review --fix` on the branch, judging code against the coding guides.
Skip a fix that would reverse a deliberate design decision, and note it as rejected.

## 2. Cleanup

Run `/simplify` on the branch, including the checklist and anti-patterns from the coding guides.

## 3. Goal check

Answer briefly, against the original task and every later requirement and clarification:

- Is every part of the request done?
- Knowing what you know now, would you choose the same approach again?

Fix what falls short.

## 4. Independent review

Start a subagent with general project knowledge but no prior context about the task or its implementation.

- Give it a frozen copy to review, so no file changes under it while it reads.
  - Commit the work, then add a separate worktree at that commit:
    `git worktree add --detach <scratchpad>/review HEAD`.
  - Point the agent at that folder and tell it to review `git diff <base>...HEAD` there.
  - Until it reports, make no edits on the branch and start no builds, renders or other runs that write files it may
    read.
  - Remove the worktree once its findings are verified: `git worktree remove <scratchpad>/review`.
- Give it the original task and all later requirements and clarifications **verbatim, without interpretation or
  summarization**.
  - After the conversation was compacted, its summary paraphrases the requests: take them from the session transcript
    (`~/.claude/projects/<project>/<session id>.jsonl`) instead.
  - Collect the user entries' text, and also messages sent while you were working, which sit inside tool results after
    "The user sent a new message while you were working:".
  - Write them in order to a file in the scratchpad and point the agent at it. Note which ones had images attached
    that it cannot see, and add, marked as context only, any message of yours that short replies such as "1. do it"
    answer.
- Tell it the base branch and the coding guide paths, and ask it to check that the changes meet every requirement and
  lose nothing the base had that the requirements did not ask to remove.
- Don't share findings from earlier steps or implementation decisions beyond what the requirements state explicitly.

Verify each finding before acting on it: for a claim that no check covers a fix, undo the fix and see whether the
checks fail.
Implement findings that give a meaningful improvement without reversing deliberate decisions or intentional omissions.

## 5. Checks and report

Run the project's documented checks and report any that could not run.

Give a concise summary:

- **Implemented changes:** one bullet per change, with a one-line summary, the step that found it, and up to two
  sentences on the improvement.
- **Rejected findings:** briefly why each finding was not addressed: an intentional omission, an unnecessary change or
  a scope limit.
- **Deferred work:** for an improvement rejected because of its size or scope, a standalone follow-up task with:
  - **Title:** short, descriptive summary.
  - **Scope:** what needs to be done.
  - **Rationale:** why the change is valuable.
  - **Reason for deferral:** why it was not included in the current change.
