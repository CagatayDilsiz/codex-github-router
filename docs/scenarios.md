# Scenarios

This guide walks through the common workflows CGR supports, with copyable examples. It assumes autonomous mode is enabled (`cgr auto on`) and that `cgr doctor` reports a healthy setup.

A short reminder of the label-driven lifecycle:

- **Issue states** (`states`): configuration keys `ready` → `inProgress` → `completed`, plus `blocked`, `needsInfo`, and `abandoned`. Default labels `codex:ready`, `codex:working`, `codex:done`, `codex:blocked`, `codex:needs-info`, `codex:abandoned`.
- **Pull-request states** (`pullRequestStates`): `reviewRequested`, `changesRequested`, `awaitingMerge`, `deferred`. Default labels `codex:rr`, `codex:cr`, `codex:merge-ready`, `codex:deferred`.
- **Repository gate** (`policies.repositoryGate`): default label `codex:gate`.

Transitions to a valid target state remove workflow labels for every other state in the same domain while preserving unrelated labels. Configuration keys are camelCase JSON names; the CLI also accepts friendlier aliases such as `working`, `done`, and `needs-info` — see [configuration.md](configuration.md) for the full reference.

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

   Transitions are label mutations only — they never acquire or ensure a work claim. Claiming happens through the hook. Note that transitioning a claimed issue to `blocked`, `needs-info`, or `abandoned` releases that issue's claim.

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
- A hook claims work only when the current model belongs to the selected worker; a mismatch blocks that prompt with a diagnostic.
- `cgr auto on` provisions configured worker labels alongside the workflow labels.
- `cgr work status` reports the resolved worker and model for newer claims.

Verify the model → worker resolution without triggering the hook:

```bash
cgr doctor --model gpt-5-codex
```

This adds a `Worker Routing` check showing which configured worker accepts the current model. It only resolves **model → worker**; it does not compare against any selected issue — issue/model eligibility is enforced by the hook when claiming. Example output:

```text
[PASS] Worker Routing: Default worker: 'luna'. Configured workers: luna, terra. Current model 'gpt-5-codex' resolves to worker 'luna'.
[WARN] Worker Routing: Default worker: 'luna'. Configured workers: luna, terra. Current model 'unknown-model' resolves to worker '<none>'.
```

## Assignee-aware routing

Assignee-aware routing is opt-in. Configure `policies.assignmentRouting` (see [configuration.md](configuration.md)); when it is absent, existing routing is unchanged.

```json
{
  "policies": {
    "assignmentRouting": {
      "mode": "require",
      "unassigned": "exclude"
    }
  }
}
```

The current identity is machine-local state. Point each machine at its GitHub usernames with a comma-separated git config value (a repository-local value overrides the global one), and keep the shared `mode`/`unassigned` policy in the repository override (`.codex-github-router/workflow.json`):

```
git config --global codex-github-router.identity "alice-mac, alice-work"
```

The repository `workflow.json` never defines the identity.

Behavior highlights:

- With `require`, the router only claims issues where one of your Git-config GitHub usernames is an assignee; `unassigned: exclude` additionally skips unassigned issues.
- With `prefer`, your assigned issues are selected first, unassigned second, and other developers' issues last — without blocking. Discovery is tiered (assigned-to-me, then unassigned when allowed, then a bounded general scan) so an unassigned issue is always chosen over another developer's even when it sits beyond the first discovery window. Merged assigned-to-me results keep the configured sort order.
- Assignment applies to all issue-derived developer work, including the `ClosedWithoutMerge` and `UnknownPullRequestState` blocker states: another developer's broken issue cannot block a strict-routing session.
- When the Git-config key is absent, the identity falls back to the authenticated GitHub account (`gh api user .login`). If the identity is unresolved in `prefer`/`require` mode, the hook blocks with a diagnostic instead of accidentally routing someone else's work.
- Assignment routing composes with worker routing: an issue must be eligible under **both** policies to be routed.
- Repository gates and continuation of your active work claim ignore assignment state (assignment is opt-in per selector).

`cgr doctor` shows the resolved settings:

```text
[PASS] Assignment Routing: Mode: 'require'. Unassigned policy: 'exclude'. Local identity: 'alice-mac, alice-work' (Git config key 'codex-github-router.identity').
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

## Explaining routing decisions

`cgr work list` and `cgr explain` are strictly read-only. They produce the same routing plan the hook evaluates — repository gate, then completed, in-progress, and ready discovery — and explain each issue stage by stage:

```bash
cgr work list                  # all rules and their verdicts, ordered like the hook would route
cgr work list --model gpt-5-codex   # same list for a specific model (like `cgr explain --model`)
cgr explain --issue 12         # detailed per-issue explanation
cgr explain                    # the same list form as `cgr work list`
cgr explain --issue 12 --model gpt-5-codex
```

The per-issue explanation covers the identical stages the hook uses to route: Workflow State, Candidate Discovery, Worker Routing, Assignment Routing, Repository Gate, Work Claim, and Routing Outcome (which shows the actual production winner, a production-wide block, or why a competing issue won). Hard ineligibility (for example a worker/model mismatch under `require`, or a blocked/needs-info repository gate) marks the issue ineligible; soft verdicts show an issue that is eligible but ranked or routed behind the production selection.

Diagnostics resolve assignment identity through the same fail-closed plan stage the hook uses: when assignment routing requires an identity and neither the CGR Git identity nor the authenticated GitHub account resolves one, the command fails with the same message the hook would block on instead of reporting assignment-ineligible issues. A claim that production reconciliation would release (a blocked/needs-info/abandoned/closed/missing claimed issue, or a missing/passive/terminal claimed pull request — including a passive pull request production would first associate with a claim that has no PR number yet) is reported as "would be released by production reconciliation; ordinary routing continues" — a read-only simulation that never touches the claim file.

Example:

```text
  #12 (Fix signup bug) - ELIGIBLE
    Selection rank: 0
    Task: NewIssue
    [+] Workflow State: Issue #12 is Ready (labels: codex:ready).
    [+] Candidate Discovery: Issue #12 was discovered by the production routing scan.
    [*] Assignment Routing: Issue #12 is assigned to current identity (alice). Rank: highest priority.
    [+] Repository Gate: Issue #12 is not gated.
    [+] Work Claim: No active work claim.
    [*] Routing Outcome: Selected: production selected issue #12 as the next work item (task: NewIssue).
```

## Worktrees and per-worktree coding claims

CGR keeps a **repository-wide claim set** in the repository's shared Git common directory, with **at most one active coding claim per worktree**. The claim file, the autonomous marker, and the structured hook diagnostics all live in the common directory (`git rev-parse --git-common-dir`) and are shared by all worktrees; the working files stay in the checked-out tree. A worktree is identified by a **relocation-safe identity**: the main worktree is the stable sentinel `.` and each linked worktree is identified relative to the common directory (for example `worktrees/<name>`). This keeps ownership intact when the repository directory itself is moved or renamed. The absolute Git directory (`git rev-parse --absolute-git-dir`) is recorded as diagnostic-only metadata and never used for identity matching or staleness.

Consequences:

- Every worktree observes the full claim set. A worktree sees its own active claim when routing, so parallel work can proceed: different worktrees can claim different issues independently.
- A worktree cannot claim work already owned by another worktree, and cannot start a second issue, branch, or pull request while it already owns an active claim.
- **Routing skips work owned by other worktrees.** Work another worktree has claimed is treated as occupied: the router selects the next eligible item and `cgr explain` reports it as hard-ineligible (`Other Worktree Claims`). The repository gate stays orthogonal to these claims: peer claims decide *who may work a gated item*, while the gate itself decides *whether unrelated work may run*. When every gated task is owned by another worktree the gate remains in force and unrelated ordinary routing is blocked (matching the `configuration.md` gate contract that gated work blocks unrelated prompts) instead of falling through.
- **Claims are a final gate, not the only gate.** Routing asks for a claim only for the item it already selected; if a concurrent worktree claimed that item between evaluation and acquisition, the hook re-evaluates once and routes the next eligible item instead of blocking.
- A claim is released by whoever proves it is releasable, from any worktree: **issue and pull-request transitions release the matching repository-wide claim** (guard-railed so only the claim whose work matches the transition is released), and `cgr work reconcile` runs **repository-wide** (it prunes claims whose worktree no longer exists and releases every claim GitHub shows as releasable across all worktrees).
- Worktree identities are compared after normalization, so trailing-separator and path variants resolve to the same worktree, and the main worktree is recognized under its stable sentinel. Since stored identities are stable, a moved or renamed repository keeps its claims: the main worktree is not classified stale just because its absolute path changed.
- If a worktree is deleted (e.g. `git worktree remove`), its claims are released by `cgr work reconcile`, which prunes claims whose worktree's Git directory no longer exists. Read-only diagnostics use the **same stale-worktree evaluation**: `cgr work status`, `cgr work list`, and `cgr explain` exclude a deleted worktree's claim (so its work is free to route) without writing to the claim file.
- Legacy claim files (single-claim format) migrate automatically to the worktree-scoped claim set on the first read, assigned to the main worktree regardless of which worktree triggers the read.

If you need to understand what the current claim is, inspect before mutating:

```bash
cgr work status        # read-only
```

Only `cgr work reconcile` (repository-wide: removes claims GitHub shows as releasable — blocked/needs-info/abandoned/closed/missing issue, or a missing/passive/terminal claimed pull request — and prunes claims whose worktree no longer exists) and `cgr work release --issue <number>` (explicit user recovery, only the supplied issue) mutate claim state. Issue and pull-request transitions also release their matching claim when the transition proves the claim is releasable. See [troubleshooting.md](troubleshooting.md) for guidance on when to use them.
