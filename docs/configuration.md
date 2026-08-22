# Configuration reference

Codex GitHub Router (`cgr`) reads a single workflow configuration that describes the GitHub issue/pull-request state machine, routing policies, and diagnostics policy. This reference documents the fields, built-in defaults, repository overrides, effective merge behavior, and validation rules.

## Where configuration lives

| Scope | Location | Created by |
| --- | --- | --- |
| Global | `~/.codex-github-router/workflow.json` | `cgr init` (the only path that creates it) |
| Repository override | `<repo>/.codex-github-router/workflow.json` | you, checked into the repository |
| Effective | merge of global + repository override | computed at runtime, never stored |

The effective configuration is what every feature consumes: the hook, `cgr auto on`, `cgr config validate`, `cgr doctor`, `cgr work`, and the issue/pr commands.

**Read-only rule:** effective configuration consumers never create a missing global workflow file. When the global file is missing, CGR uses the built-in defaults in memory. Only `cgr init` writes the global file.

## Inspecting configuration

The `config` commands are read-only and never create a missing configuration file:

```bash
cgr config path                     # print the global path; print the repository override path if the file exists
cgr config show                     # print the stored global configuration as JSON
cgr config show --effective         # print the effective (merged) configuration as JSON
cgr config validate                 # validate the effective configuration
```

- `cgr config show` without `--effective` prints only the stored global configuration.
- `cgr config show --effective [working-directory]` prints the merged result for a repository.
- `cgr config validate` loads the effective configuration and prints `Configuration is valid.` (exit `0`) or `Configuration is invalid: <reason>.` (exit `1`).

`cgr doctor` also reports the presence and validity of the global configuration, the repository override, and the effective configuration.

## Top-level structure

```json
{
  "version": 1,
  "states": { },
  "pullRequestStates": { },
  "defaultIssueSelection": { },
  "policies": { }
}
```

The `version` field must be `1`. Any other value fails validation with `Unsupported workflow configuration version`.

## Issue workflow states (`states`)

Each issue state is a list of rules; rules are **ORed** — any configured label for a state matches that state, and multiple matching labels for the same state are valid. Defaults:

| Configuration key | Default labels | Meaning |
| --- | --- | --- |
| `ready` | `codex:ready` | actionable next work |
| `inProgress` | `codex:working` | actively in progress |
| `completed` | `codex:done` | completed |
| `blocked` | `codex:blocked` | blocked |
| `needsInfo` | `codex:needs-info` | waiting for information |
| `abandoned` | `codex:abandoned` | abandoned / terminal |

> Configuration keys are camelCase JSON property names (`inProgress`, `completed`, `needsInfo`). They are distinct from the human-friendly aliases accepted by the CLI (`cgr issue list --state working`, `cgr issue transition 5 done`): the CLI accepts `ready`/`ready-to-start`/`begin`, `working`/`in-progress`/`inprogress`, `completed`/`done`, `blocked`, `needs-info`/`need-info`/`needsinfo`, and `abandoned`.

Example: a repository that uses its own ready label:

```json
{
  "states": {
    "ready": [
      {
        "type": "label",
        "values": ["project:ready"]
      }
    ]
  }
}
```

## Pull-request states (`pullRequestStates`)

Same rule shape and OR semantics. Defaults:

| Configuration key | Default labels | CLI aliases (`cgr pr transition`) | Meaning |
| --- | --- | --- | --- |
| `reviewRequested` | `codex:rr` | `review-requested`, `ready-for-review` | awaiting review |
| `changesRequested` | `codex:cr` | `changes-requested` | change request pending |
| `awaitingMerge` | `codex:merge-ready` | `awaiting-merge` | ready to merge |
| `deferred` | `codex:deferred` | `deferred` | intentionally deferred |

Pull-request states are evaluated against **open** pull requests. A `merged` or `closed` pull request is terminal and bypasses label-based PR state.

## Issue selection (`defaultIssueSelection`)

```json
{
  "defaultIssueSelection": {
    "limit": 1,
    "sortBy": "createdAt",
    "direction": "ascending"
  }
}
```

- `limit` must be greater than zero (validation).
- `sortBy`: `createdAt` / `updatedAt`.
- `direction`: `ascending` / `descending`.

## Policies (`policies`)

### `autonomousActivation`

Controls when the hook may route prompts.

```json
{
  "policies": {
    "autonomousActivation": {
      "mode": "always",
      "prompts": ["work on the next task"]
    }
  }
}
```

- `mode`: `always` (default) or `prompt`.
- `prompts`: required for `prompt` mode (at least one); ignored by `always`.

In `prompt` mode the hook only activates when the submitted prompt matches a configured prompt exactly after normalization: Unicode NFC normalization, trimmed and collapsed Unicode whitespace, one trailing ASCII period ignored, compared full-string with ordinal case-insensitive equality. Empty prompts are rejected. The Codex scheduled heartbeat envelope (a `<heartbeat>` payload with a single `<instructions>` element) is unwrapped before matching, so scheduled automation can activate the same prompt gates. `cgr auto status` shows the active mode and configured prompts.

### `workerRouting`

Opt-in, model-aware worker routing. When the `workerRouting` object is present, routing is enabled.

```json
{
  "policies": {
    "workerRouting": {
      "defaultWorker": "luna",
      "workers": {
        "luna": {
          "labels": ["codex:worker:luna"],
          "models": ["gpt-5-codex"]
        },
        "terra": {
          "labels": ["codex:worker:terra"],
          "models": ["gpt-5-mini"]
        }
      }
    }
  }
}
```

- `defaultWorker`: the worker used for issues with no worker label.
- `workers`: worker profiles, each with at least one `labels` entry and one `models` entry.
- Worker labels **must** use the `codex:worker:` namespace; unknown worker labels fail closed.

Resolution rules: an unlabeled issue uses the default worker; one worker label selects that profile; multiple worker labels are conflicting and rejected; unknown worker labels are rejected. A hook claims work only when the current model belongs to the selected worker. `cgr doctor --model <model>` resolves **model → worker** only and reports `WARN` when no configured worker accepts the model; it does not compare against a selected issue — issue/model eligibility is enforced by the hook at claim time. Pull-request change requests inherit the worker selected by their linked issue; a pull request closing issues assigned to different workers is a conflict.

Validation: a default worker is required, worker names are case-insensitively unique, labels must not be shared between workers, models must not be shared between workers, and the default worker must exist.

### `repositoryGate`

Orthogonal gate policy that can block unrelated work.

```json
{
  "policies": {
    "repositoryGate": {
      "labels": ["codex:gate"]
    }
  }
}
```

Configured gate labels are ORed. A gate is evaluated after the active work claim is reconciled and before ordinary routing. Gated ready, interrupted working, and change-request work is prioritized and claimed; gated review/merge/blocked/needs-info/deferred/unresolved work blocks unrelated prompts. Merged pull requests and abandoned/closed issues are terminal and do not keep a gate active. State transitions preserve gate labels. `cgr work status` reports repository gates separately from the active claim.

### `diagnostics`

Controls the structured hook diagnostic trail.

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

- `enabled`: `true` (default) writes one record per hook invocation.
- `retentionDays`: must be at least `1` (validation); expired records are pruned during each invocation.

## Merge semantics (global + repository override)

A repository can override only the fields that differ from the global configuration by adding `.codex-github-router/workflow.json` at the repository root.

- CGR loads the complete global configuration first (or the in-memory defaults when the global file is missing).
- Repository objects are **recursively merged**: nested objects merge property by property; supplied scalar and array values **replace** inherited values.
- Arrays are **never concatenated**.
- Explicit `null` values in the repository file are rejected.
- The result is validated as the effective configuration.

Example override that changes the ready label and activation prompts while inheriting everything else:

```json
{
  "states": {
    "ready": [
      {
        "type": "label",
        "values": ["project:ready"]
      }
    ]
  },
  "policies": {
    "autonomousActivation": {
      "prompts": ["sıradaki görevi yapabilir miyiz"]
    }
  }
}
```

The checked-out working tree is the source of the repository override. Invalid JSON or an invalid merged configuration fails **before** claim acquisition, GitHub discovery, label provisioning, or workflow transitions.

## Validation rules

`cgr config validate`, `cgr doctor`, `cgr auto on`, and the hook itself all run the same centralized validation of the effective configuration:

- `version` must be `1`.
- Every issue workflow state must contain at least one rule.
- Every pull-request state must contain at least one rule.
- `defaultIssueSelection.limit` must be greater than zero.
- `autonomousActivation.mode` must be `always` or `prompt`; `prompt` requires at least one non-empty prompt with no duplicates after normalization.
- `workerRouting`, when present, must satisfy the routing validation described above.
- Labels must not conflict: a label cannot map to multiple states within the same domain (issue or pull request); repository gate labels must not also be workflow labels; worker labels must not be workflow or gate labels; label names must not have leading/trailing whitespace.
- `diagnostics.retentionDays` must be at least one.

## Built-in defaults (global file missing)

When the global workflow file does not exist, the effective configuration equals the built-in defaults: the default state and pull-request label mappings, `defaultIssueSelection` limit `1`, `autonomousActivation` mode `always`, `repositoryGate` label `codex:gate`, `workerRouting` disabled (no object), and `diagnostics` enabled with `retentionDays` 7.

## Effective diagnostics policy

The diagnostics policy is resolved from the effective workflow configuration as soon as CGR loads it, covering activation decisions and everything after them. Wrong-event and autonomous-disabled bypasses occur before configuration loading, so their records resolve the diagnostics policy best-effort from the effective configuration of the current working directory (repository overrides included); `enabled: false` disables the trail entirely, an invalid policy (for example `retentionDays: 0`) falls back to the defaults, and a failed resolution never changes hook behavior.
