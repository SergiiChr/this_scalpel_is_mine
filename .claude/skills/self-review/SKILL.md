---
name: self-review
description: >-
  Review a finished branch against its base, get an independent review from an agent without the task's context,
  and implement what improves it.
  Use before handing off a change for merge (Development in docs/RELEASE.md).
---

# Self-review

## 1. Review the current branch

Review all changes against the base branch, considering the original goals, subsequent changes, clarifications and
adjustments.

- **Goal completion:** Has the original goal been achieved? Could it be accomplished more effectively or efficiently
  through a more robust implementation or better-targeted features? Given everything known now, would the same solution
  be chosen when starting from scratch?
- **Workarounds and inefficiencies:** Are there temporary solutions, duplicated logic or inefficiencies that can now be
  replaced with dedicated solutions? Consider shared code, common setup, cleanup and documentation consolidation.
- **Project integration:** How well does the implementation fit into the broader project? Could it be integrated more
  naturally through generalized tools, shared utilities, code reuse or better alignment with existing architecture and
  conventions?
- **Accumulated technical debt:** Have iterative changes, revised requirements or debugging introduced unnecessary
  complexity, redundant abstractions, obsolete code paths or temporary fixes? Can these now be simplified or removed
  with the benefit of knowing the final requirements?

## 2. Perform an independent review

Start a subagent with general project knowledge but no prior context about the task or its implementation.

- Give it the original task and all subsequent requirements and clarifications **verbatim, without interpretation or
  summarization**.
- Ask it to review the changes against the base branch independently.
- Don't share findings from step 1 or implementation decisions beyond what the requirements state explicitly.

## 3. Evaluate findings and implement improvements

Weigh the findings of both steps and implement the changes that give a meaningful improvement without reversing
deliberate design decisions or intentional omissions.

Give a concise summary of what was changed, what was rejected and why.

- **Implemented changes:** one bullet per change, with a one-line summary and up to two sentences on the improvement.
- **Rejected findings:** briefly why each finding was not addressed: an intentional omission, an unnecessary change or a
  scope limit.
- **Deferred work:** for an improvement rejected because of its size or scope, a standalone follow-up task with:
  - **Title:** short, descriptive summary.
  - **Scope:** what needs to be done.
  - **Rationale:** why the change is valuable.
  - **Reason for deferral:** why it was not included in the current change.
