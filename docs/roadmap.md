# Roadmap

This page records intended capabilities at a high level. It deliberately does not lock down implementation design. Nothing here is a commitment or a release plan.

## Parallel work across multiple worktrees

CGR keeps a repository-wide claim set with one active coding claim per worktree, so multiple issues can be worked on concurrently — one issue per worktree — with the router coordinating which session owns which issue and branch within each worktree, and blocking worktree duplication across the repository. Remaining gaps are cross-machine coordination (see the multi-developer routing section below) and surfacing the full claim set in more surfaces.

## Multi-developer routing with shared coordination

Assignee-aware routing (`policies.assignmentRouting`) now routes the current session toward issues it owns, using GitHub assignees with `ignore`/`prefer`/`require` modes, an explicit unassigned policy, local identity resolution, and fail-closed diagnostics. Remaining gaps are shared coordination: keeping claimed issues assigned to their owning developer across machines (unassigning a claimed issue would make it *more* eligible for other developers under `prefer`/`require`), reviewer-based routing, and cross-worktree ownership management so multiple developers do not claim the same issue.

## Native GitHub review/check signals in workflow evaluation

Use native GitHub signals — reviews, status checks, mergeable state — in workflow evaluation instead of relying only on labels. For example, "all checks green" or "approved review" could drive the pull-request lifecycle automatically.

## Pull-request review as a claimable work type

Pull-request review is now a claimable work type under `policies.reviewRouting.enabled`, claimed per (pull request, reviewer) with a submitted-review cycle marker, fail-closed release, and diagnostics through `cgr work list`, `cgr explain --pr`, and `cgr work reconcile`. Remaining gaps are team-review membership resolution (team review requests are exposed for diagnostics but not claimable) and review routing across the local identity's aliases when those aliases are not the authenticated GitHub account.

## Richer configuration editing commands

The current stable surface is read-only inspection (`cgr config path/show/validate`). Future commands could edit configuration safely with validation, or manage policies interactively, without encouraging manual file edits.

## Broader status / explain diagnostics

`cgr work list` and `cgr explain` already describe *why* a decision was made from the same plan the hook evaluates: workflow state resolution, candidate discovery order, worker and assignment routing, repository gates, the active claim, and the final production routing decision. Future work could add claim history and a machine-readable plan output.

## Daemon / service mode

A background poller that can run the same routing engine without waiting for a prompt hook invocation. `execution.mode` is set to `daemon`, the hook deterministically bypasses prompts for the repository, and `cgr daemon start/stop/status/restart` plus `cgr daemon run --once` drive polling, crash recovery, and clean shutdown. Claims are owned by the stable daemon session id across restarts, so restarts continue rather than duplicate in-flight work; a per-repository state file (`codex-github-router.daemon.json`) records the session, health, last cycle, and the supervised Codex session. The chosen shape and its caveats are issue #54 design notes in the repository issues.
