# Scenarios

This guide walks through the common workflows CGR supports, with copyable examples. It assumes autonomous mode is enabled (`cgr auto on`) and that `cgr doctor` reports a healthy setup.

A short reminder of the label-driven lifecycle:

- **Issue states** (`states`): `ready` → `working` → `done`, plus `blocked`, `needs-info`, and `abandoned`. Default labels `codex:ready`, `codex:working`, `codex:done`, `codex:blocked`, `codex:needs-info`, `codex:abandoned`.
- **Pull-request states** (`pullRequestStates`): `reviewRequested`, `changesRequested`, `awaitingMerge`, `deferred`. Default labels `codex:rr`, `codex:cr`, `codex:merge-ready`, `codex:deferred`.
- **Repository gate** (`policies.repositoryGate`): default label `codex:gate`.

Transitions to a valid target state remove workflow labels for every other state in the same domain while preserving unrelated labels. See [configuration.md](configuration.md) for the full reference.

## Implementing a ready issue

1. Create an issue on GitHub and label it `codex:ready`.
2. In Codex, inside the repository, submit:

   > work on the next task

3. CGR finds the ready issue, acquires the repository work claim, and returns the issue identity as additional context. Codex starts a branch named `codex/issue-<number>-<short-description>` and implements the issue.
4. When a pull request is opened, apply the review workflow label (`codex:rr` by default) with:

   ```bash
   cgr pr transition <number> review-requested
   ```

5. Move the issue to `working`:

   ```bash
   cgr issue transition <number> working
   ```

   Transitioning to `working` also ensures the repository still has a single active claim for this issue.

Inspect what is ready:

```bash
cgr issue list                      # open issues
cgr issue list --state Ready        # issues matching the ready labels
cgr issue list --use-configured     # issues matching configured filters
```

## Continuing an in-progress issue

If a session is interrupted, the claim and the `codex:working` issue remain. On the next routed prompt CGR detects the in-progress issue and tells Codex to **resume** that issue rather than start something new. CGR only recovers branches that match the `codex/issue-<number>-<short-description>` prefix, then checks the linked pull requests for the recovered branch before allowing work to continue.

Inspect the current state:

```bash
cgr work status          # active claim, worker/model, and any repository gate
cgr issue list --state InProgress
```

If a working issue has linked open pull requests that are all `deferred`, it is non-blocking and ready work may proceed. Multiple `working` issues are an ambiguous workflow state and block the hook until resolved.

## Handling a pull-request change request

When a pull request gets the `changesRequested` label (`codex:cr` by default), CGR prioritizes that change request over ready work:

> work on the next task

CGR routes the prompt to the change-requested pull request, returns it as additional context, and records a claim. A change request inherits the worker selected by its linked issue. The hook will keep routing to the change request until the pull request is merged, closed, or moved to another pull-request state.

```bash
cgr pr list                          # open pull requests
cgr pr transition <number> changes-requested
cgr pr transition <number> awaiting-merge
```

Merged pull requests produce a `CloseIssue` task so the linked issue can be closed automatically. Closed-without-merge pull requests block until reviewed.

## Worker / model routing

Worker routing is opt-in. Configure `policies.workerRouting` (see [configuration.md](configuration.md)); when it is absent, existing routing is unchanged.

- An issue with **no** worker label is routed to the `defaultWorker`.
- An issue with **one** `codex:worker:*` label is routed to that worker profile.
- **Multiple** worker labels, or unknown worker labels, are rejected (fail closed).
- A hook claims work only when the current model belongs to the selected worker.
- `cgr auto on` provisions configured worker labels alongside the workflow labels.
- `cgr work status` reports the resolved worker and model for newer claims.

Verify routing for a model without triggering the hook:

```bash
cgr doctor --model gpt-5-codex
```

This adds a `Worker Routing` check showing whether the model resolves to a worker and whether that worker owns the currently selected issue. Example output:

```text
[PASS] Worker Routing: Enabled; model 'gpt-5-codex' resolves to worker 'luna'.
```

## Prompt-gated and scheduled automation

By default autonomous activation is `always`: every `UserPromptSubmit` is eligible for routing. To require an exact gate, configure `policies.autonomousActivation` with `mode: "prompt"` and at least one prompt:

```json
{
  "policies": {
    "autonomousActivation": {
      "mode": "prompt",
      "prompts": ["sıradaki görevi yapabiliriz", "work on the next task"]
    }
  }
}
```

Matching is exact after normalization (see [configuration.md](configuration.md)). Empty prompts silently bypass the hook; non-matching prompts do not inspect claims or query GitHub.

Scheduled automation works through the same gate: Codex's scheduled heartbeat (`<heartbeat>` with a single `<instructions>` element) is unwrapped before matching, so the configured prompts can activate routing from a schedule too. `cgr auto status` shows the active activation mode and configured prompts:

```bash
cgr auto status
```

## Repository gates

A critical issue or pull-request workstream can block unrelated work with `policies.repositoryGate` (default label `codex:gate`):

```json
{
  "policies": {
    "repositoryGate": {
      "labels": ["codex:gate"]
    }
  }
}
```

- Gated ready, interrupted working, and change-request work is prioritized and claimed.
- Gated review, merge, blocked, needs-info, deferred, or unresolved work blocks unrelated prompts with a diagnostic that includes how to unblock (remove the gate label).
- Merged pull requests and abandoned/closed issues are terminal and do not keep a gate active.
- State transitions preserve gate labels; the gate is evaluated after claim reconciliation and before ordinary routing.

```bash
cgr work status        # reports repository gates separately from the active claim
```

## Worktrees and the single-active-claim limitation

CGR keeps **at most one active coding claim** in the repository's shared Git common directory, so every worktree observes the same owner. The claim file, the autonomous marker, and the structured hook diagnostics all live in the common directory (`git rev-parse --git-common-dir`) and are shared by all worktrees; the working files stay in the checked-out tree.

Consequences:

- A prompt from any worktree sees the same active claim. If another Codex session owns it, the hook blocks with a diagnostic rather than starting parallel work.
- CGR never starts a second issue, branch, or pull request while a claim is active.
- **Parallel work across multiple worktrees is not supported yet.** This is the single-active-claim limitation; see [roadmap.md](roadmap.md).

If you need to understand what the current claim is, inspect before mutating:

```bash
cgr work status        # read-only
```

Only `cgr work reconcile` (removes claims GitHub shows as passive/terminal) and `cgr work release --issue <number>` (explicit user recovery, only the supplied issue) mutate claim state. See [troubleshooting.md](troubleshooting.md) for guidance on when to use them.
