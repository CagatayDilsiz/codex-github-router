# Roadmap

This page records intended capabilities at a high level. It deliberately does not lock down implementation design. Nothing here is a commitment or a release plan.

## Parallel work across multiple worktrees

CGR keeps a repository-wide claim set with one active coding claim per worktree, so multiple issues can be worked on concurrently — one issue per worktree — with the router coordinating which session owns which issue and branch within each worktree, and blocking worktree duplication across the repository. Remaining gaps are cross-machine coordination (see the multi-developer routing section below) and surfacing the full claim set in more surfaces.

## Multi-developer routing with shared coordination

Assignee-aware routing (`policies.assignmentRouting`) now routes the current session toward issues it owns, using GitHub assignees with `ignore`/`prefer`/`require` modes, an explicit unassigned policy, local identity resolution, and fail-closed diagnostics. Remaining gaps are shared coordination: keeping claimed issues assigned to their owning developer across machines (unassigning a claimed issue would make it *more* eligible for other developers under `prefer`/`require`), reviewer-based routing, and cross-worktree ownership management so multiple developers do not claim the same issue.

## Native GitHub review/check signals in workflow evaluation

Use native GitHub signals — reviews, status checks, mergeable state — in workflow evaluation instead of relying only on labels. For example, "all checks green" or "approved review" could drive the pull-request lifecycle automatically.

## Pull-request review as a claimable work type

Today change requests on linked pull requests are routed, but PR review itself is not a claimable work type. A future capability is claiming and performing review work explicitly.

## Richer configuration editing commands

The current stable surface is read-only inspection (`cgr config path/show/validate`). Future commands could edit configuration safely with validation, or manage policies interactively, without encouraging manual file edits.

## Broader status / explain diagnostics

`cgr work list` and `cgr explain` already describe *why* a decision was made from the same plan the hook evaluates: workflow state resolution, candidate discovery order, worker and assignment routing, repository gates, the active claim, and the final production routing decision. Future work could add claim history and a machine-readable plan output.

## Daemon / service mode

A future daemon/service mode that can poll GitHub and Codex, manage sessions, and act on schedule or events instead of only responding to the prompt hook.
