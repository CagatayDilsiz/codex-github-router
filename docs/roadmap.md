# Roadmap

This page records intended capabilities at a high level. It deliberately does not lock down implementation design. Nothing here is a commitment or a release plan.

## Parallel work across multiple worktrees

CGR currently keeps a single active coding claim per repository, shared across worktrees. A future capability is parallel work where multiple issues can be worked on concurrently — for example one issue per worktree — with the router coordinating which session owns which issue and branch.

## Multi-developer routing with shared coordination

Route work to the right session or developer using assignment and reviewer signals, with shared coordination so multiple developers do not claim the same issue. Worker routing today is model-based and opt-in; this extends routing to human assignment/review signals.

## Native GitHub review/check signals in workflow evaluation

Use native GitHub signals — reviews, status checks, mergeable state — in workflow evaluation instead of relying only on labels. For example, "all checks green" or "approved review" could drive the pull-request lifecycle automatically.

## Pull-request review as a claimable work type

Today change requests on linked pull requests are routed, but PR review itself is not a claimable work type. A future capability is claiming and performing review work explicitly.

## Richer configuration editing commands

The current stable surface is read-only inspection (`cgr config path/show/validate`). Future commands could edit configuration safely with validation, or manage policies interactively, without encouraging manual file edits.

## Broader status / explain diagnostics

Beyond `cgr doctor` and structured hook diagnostics: richer status and explain commands that describe *why* a decision was made, including workflow state resolution, routing decisions, and claim history.

## Daemon / service mode

A future daemon/service mode that can poll GitHub and Codex, manage sessions, and act on schedule or events instead of only responding to the prompt hook.
