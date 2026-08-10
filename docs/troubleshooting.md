# Troubleshooting

**Start with `cgr doctor`.** It is strictly read-only and never creates, modifies, or deletes configuration, hooks, claims, labels, or any repository file. It reports independent checks so a single problem never hides the other results.

```bash
cgr doctor
cgr doctor /path/to/repository
cgr doctor --model gpt-5-codex      # adds a worker-routing check for the current model
```

## Reading a doctor report

Each check reports `PASS`, `WARN`, or `FAIL`, followed by a detail and an actionable recommendation for failing or warning checks. Exit codes:

| Exit code | Meaning |
| --- | --- |
| `0` | all checks pass, or only warnings |
| `1` | at least one required setup check failed |
| `2` | usage error (bad arguments) |

User-level checks: CGR version, .NET runtime, Git, GitHub CLI availability and authentication, the Codex hooks file (presence and valid JSON), a single registered `cgr hook` entry, and the global workflow configuration.

Repository-level checks: valid Git repository and common directory, the repository override at `.codex-github-router/workflow.json`, the effective workflow configuration, autonomous mode, the active work claim, required labels, and worker routing with `--model`.

The report never prints session identifiers, authentication material, or prompt contents; the work-claim summary shows only work identity (issue, pull request, type, worker, model).

## Common problems and recovery

### Missing or malformed Codex hooks configuration

**Symptom:** `[FAIL] Codex Hooks Configuration` (`Not found:` or `Not valid JSON:`) and `[FAIL] CGR Hook Entry`.

**Inspection (read-only):**

```bash
cgr doctor
cgr config path
```

**Recovery (mutation):** run `cgr init` to (re)create the hooks file and register the hook. `cgr init` preserves unrelated hooks and backs up the previous file before an update. If the file is malformed, repair it or let `cgr init --force` rewrite the CGR entry, then restart Codex.

> The missing hooks file itself reports `WARN`, but the derived `CGR Hook Entry` check is `FAIL`: without a registered hook the router cannot run through Codex, and warnings-only reports exit `0`. Only fixing the entry moves the report to a clean exit.

### Missing, duplicate, or stale CGR hook registration

**Symptom:** `[FAIL] CGR Hook Entry` ("No 'cgr hook' entry found") or `[WARN] CGR Hook Entry` ("2 'cgr hook' entries found").

**Recovery (mutation):**

- No entry: `cgr init` (or `cgr init --force` if the entry exists but is stale).
- Duplicate entries: remove the duplicates from `~/.codex/hooks.json` or run `cgr init --force`, which replaces the CGR command blocks. Unrelated hook groups and handlers are preserved.
- Verify: `cgr doctor` should report exactly one entry.

### Missing Git / GitHub CLI, or unauthenticated `gh`

**Symptom:** `[FAIL] Git`, `[FAIL] GitHub CLI`, or `[FAIL] GitHub CLI Authentication`.

**Recovery (mutation):**

```bash
# install Git and gh, then authenticate:
gh auth login
gh auth status    # verify
```

`cgr doctor` continues other checks even when these fail, so a missing `gh` never hides a hooks or configuration problem.

### Invalid global, repository, or effective workflow configuration

**Symptom:** `[FAIL] Global Workflow Configuration`, `[FAIL] Repository Workflow Configuration`, or `[FAIL] Effective Workflow Configuration`.

**Inspection (read-only):**

```bash
cgr config show                 # stored global configuration
cgr config show --effective     # merged configuration
cgr config validate             # validation error message, exit 1 on invalid
```

**Recovery (mutation):** fix the reported file:

- global: `~/.codex-github-router/workflow.json`; `cgr init` rewrites the default, or edit the file directly;
- repository override: `<repo>/.codex-github-router/workflow.json` — check for invalid JSON, an unsupported `version`, explicit `null` values, or invalid effective policy values.

Remember the merge rules: arrays are never concatenated, scalars and arrays replace inherited values, and explicit `null` is rejected. Invalid configuration fails before claim acquisition, GitHub discovery, label provisioning, or workflow transitions.

### Worker / model mismatch

**Symptom:** the hook blocks with a message like `Issue #5 belongs to worker 'luna', but the current model resolves to worker 'terra'`, or `cgr doctor --model <model>` shows a `Worker Routing` failure.

**Inspection (read-only):**

```bash
cgr doctor --model gpt-5-codex
cgr work status
```

**Recovery (mutation):** align the current model with the worker owning the issue, or change the issue's `codex:worker:*` label so it matches the model's worker. Unknown or multiple worker labels fail closed; remove them. Verify with `cgr doctor --model <model>`.

### Missing required labels

**Symptom:** `[WARN] Required GitHub Labels` with `Missing label(s):`.

**Recovery (mutation):** run `cgr auto on`. It validates the workflow configuration and creates **only** the missing labels referenced by the configured rules; existing labels are never changed. Re-run it after changing the workflow configuration to provision newly required labels safely.

### Invalid or stale work claims

**Symptom:** `[FAIL] Active Work Claim` (invalid claim file) or a claim that no longer matches GitHub state.

**Inspection (read-only) first:**

```bash
cgr work status
```

`cgr work status` reports the active claim (issue, pull request, type, worker, model) and any repository gate. It never changes anything.

**Recovery (mutation), in order of preference:**

1. `cgr work reconcile` — removes only claims that GitHub shows as **passive or terminal** (for example a merged pull request). Safe when a session no longer owns the work.
2. `cgr work release --issue <number>` — explicit user recovery; removes the claim **only** for the supplied issue.

> Do **not** delete the claim file or autonomous marker blindly. An active Codex session may still own the work. Inspect with `cgr work status`, confirm no session owns the issue, and prefer `reconcile`/`release` over deleting files.

### Autonomous mode disabled or repository state inconsistent

**Symptom:** the hook returns `bypass` and never routes; `cgr auto status` shows `Autonomous mode: disabled`, or the repository workflow state conflicts.

**Inspection (read-only):**

```bash
cgr auto status
cgr doctor
cgr issue list --state InProgress
```

**Recovery (mutation):**

```bash
cgr auto on          # enable and provision missing labels
```

If multiple issues are marked `working`, that is an ambiguous workflow state and blocks the hook. Resolve the labels (move extra issues back to `ready`/`done`) rather than deleting state. See [scenarios.md](scenarios.md).

## Structured hook diagnostics

Every hook invocation writes a lightweight, machine-readable record to the repository's shared Git directory so all worktrees observe the same trail without dirtying the checked-out tree.

### Location

```text
<git-common>/codex-github-router.diagnostics/invocation-<id>.json
```

`<git-common>` is resolved with `git rev-parse --git-common-dir`. Records use an atomic temporary-file write, so concurrent hook invocations cannot corrupt or overwrite each other.

### Contents and privacy

Each record is a structured object with fields such as `eventName`, `invocationId`, `timestampUtc`, `durationMs`, `repositoryIdentity`, `autonomousEnabled`, `activationMode`, `activationResult`, `workflowItemType`, `issueNumber`, `pullRequestNumber`, `worker`, `model`, `claimId`, `result`, `blockReason`, `errorType`, `errorMessage`.

The `result` field distinguishes:

- `bypass` — wrong hook event, autonomous mode disabled, or a non-matching activation prompt;
- `context` — routing delivered additional context;
- `block` — a workflow or claim decision blocked the prompt;
- `error` — a hook failure.

Records never include full prompts, issue or pull-request bodies, generated additional context, authentication material, or complete session identifiers. Work-claim identifiers are shortened to eight characters. On unexpected errors only the exception type name is persisted; raw exception messages are never stored unless they are one of CGR's own known-safe static messages.

### Behavior guarantees

Diagnostics are **best-effort**: any directory, serialization, lock, or write failure is ignored and never changes the hook response, exit code, claim state, or GitHub mutations. The default configuration retains records for seven days, pruning expired records during each invocation.

### Configuring, disabling, and cleaning

Configuration lives in `policies.diagnostics` (see [configuration.md](configuration.md)):

```json
{
  "policies": {
    "diagnostics": {
      "enabled": true,
      "retentionDays": 7
    }
  }
}
```

- Disable the trail entirely: set `enabled` to `false`.
- Prune sooner: lower `retentionDays` (must be at least `1`).
- Clean immediately (**mutation**): delete the `codex-github-router.diagnostics` directory inside the Git common directory. These are diagnostics records only — deleting them does not affect claims, labels, hooks, or GitHub state.

The policy is applied from the effective workflow configuration (repository overrides included). Wrong-event and autonomous-disabled bypasses occur before configuration loading, so their records resolve the diagnostics policy best-effort from the current working directory; `enabled: false` disables the trail entirely and a failed resolution never changes hook behavior.

When deeper hook-execution analysis is needed, start from the diagnostic records listed above rather than reading hook output directly.
