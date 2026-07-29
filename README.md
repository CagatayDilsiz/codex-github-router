# Codex GitHub Router

> [!WARNING]
> This project is under active development and is currently in an alpha stage. Commands, configuration, and workflow behavior may change between releases.

Codex GitHub Router (`cgr`) is a small .NET CLI that connects Codex sessions with GitHub Issues and Pull Requests. It installs a Codex `UserPromptSubmit` hook, finds the next actionable workflow item, prioritizes existing review or change-request work, and can prevent new work from starting while an earlier item still requires attention.

## Requirements

* .NET 10
* [GitHub CLI](https://cli.github.com/) installed and authenticated
* Codex CLI with hooks support
* A GitHub repository that uses the configured issue and pull-request labels

Verify GitHub CLI authentication before using the router:

```bash
gh auth status
```

## Installation

Install the global .NET tool from NuGet after a package has been published:

```bash
dotnet tool install --global codex-github-router --version 0.0.1-alpha
```

Verify the installation:

```bash
cgr --version
```

## Setup

Run the initializer once:

```bash
cgr init
```

The initializer creates the default workflow configuration and adds a single `cgr hook` command to the user-level Codex hooks file. Existing hooks are preserved, and updates create a backup of the previous hooks file.

To rewrite the generated workflow configuration and refresh the CGR hook entry:

```bash
cgr init --force
```

Codex may need to be restarted after changing its hooks configuration.

## Basic usage

Run these commands from a Git repository connected to GitHub:

```bash
cgr --help
cgr issue list
cgr pr list
cgr auto status
cgr auto on
```

### Active work claims

CGR keeps at most one active coding claim in the repository Git common directory, so every worktree observes the same owner. Inspect or recover the claim with:

```bash
cgr work status
cgr work reconcile
cgr work release --issue <number>
```

`reconcile` removes only claims that GitHub shows as passive or terminal. `release` is an explicit user recovery action and only removes the claim for the supplied issue.

Model-aware worker routing is opt-in. Configure it under `policies.workerRouting` with a required default worker, worker labels, and exact model slugs:

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

Without this policy, existing routing is unchanged. Worker labels must use the `codex:worker:` namespace so unknown worker labels fail closed. With it enabled, an unlabeled issue uses the default worker, one worker label selects that profile, and multiple or unknown worker labels are rejected. A hook only claims work when its current model belongs to the selected worker; pull-request change requests inherit the worker selected by their linked issue. `cgr auto on` provisions configured worker labels alongside the workflow labels, and `cgr work status` reports the resolved worker and model for newer claims.

Autonomous mode is repository-specific. When it is enabled, the Codex hook can route prompts according to the configured GitHub issue and pull-request workflow. `cgr auto on` validates the workflow configuration and creates only missing labels referenced by its issue and pull-request label rules; existing labels are never changed. CGR stores the applied configuration fingerprint in the repository's shared Git directory so the same setup also works from Git worktrees. After changing the workflow configuration, run `cgr auto on` again to provision any newly required labels safely.

Autonomous activation defaults to `always`. To require an exact user-prompt gate, configure `policies.autonomousActivation` with `mode: "prompt"` and at least one prompt:

```json
{
  "policies": {
    "autonomousActivation": {
      "mode": "prompt",
      "prompts": [
        "sıradaki görevi yapabiliriz",
        "work on the next task"
      ]
    }
  }
}
```

Prompt matching applies Unicode NFC normalization, trims and collapses Unicode whitespace, ignores one trailing ASCII period, and then compares the full strings with ordinal case-insensitive equality. Empty prompts silently bypass the hook; non-matching prompts do not inspect claims or query GitHub. `always` ignores any configured prompt values. Invalid modes and empty prompt-gated lists fail centralized workflow validation. `cgr auto status` displays the active activation mode and configured prompts or count.

Repositories can override only the workflow fields that differ from the global configuration by adding `.codex-github-router/workflow.json` at the repository root. CGR loads the complete global configuration first, recursively merges repository objects, replaces supplied scalar and array values, then validates the effective result. Omitted values inherit the global configuration; arrays are never concatenated; explicit `null` values are rejected; and the repository file is never created or modified automatically.

For example, this repository override changes the ready label and activation prompts while inheriting every other global setting, including the activation mode:

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

The checked-out working tree is the source of the repository override. Invalid JSON or an invalid merged configuration fails before claim acquisition, GitHub discovery, label provisioning, or workflow transitions.

When a single issue is already in the configured `working` state, it takes precedence over ready issues. New work branches use `codex/issue-<number>-<short-description>`; CGR only recovers branches with that exact issue prefix, then checks pull requests for the recovered branch before allowing work to continue. CGR never starts a second issue, branch, or pull request. Multiple working issues are treated as an ambiguous workflow state and block the hook until resolved. A working issue whose linked open pull requests are all `deferred` is non-blocking, so ready work may proceed.

Within each workflow domain, label rules are ORed: any configured label for a state matches that state, and multiple matching labels for that same state are valid. Labels matching different states on the same issue or pull request are ambiguous. CGR blocks the hook with a diagnostic listing those labels rather than depending on label order. A transition to a valid target state removes workflow labels for every other state in that same domain while preserving unrelated labels.

### Repository workflow gates

Critical issue and pull-request workstreams can block unrelated work with the orthogonal repository gate policy. The default configuration uses `codex:gate` and stores it separately from workflow state labels:

```json
{
  "policies": {
    "repositoryGate": {
      "labels": ["codex:gate"]
    }
  }
}
```

Configured gate labels are ORed. A gate is evaluated after any active work claim is reconciled and before ordinary routing. Gated ready, interrupted working, and change-request work is prioritized and claimed; gated review, merge, blocked, needs-info, deferred, or unresolved work blocks unrelated prompts. Merged pull requests and abandoned or closed issues are terminal and do not keep a gate active. State transitions preserve gate labels. `cgr work status` reports repository gates separately from the active claim.

### Manual validation for working-issue recovery

The automated tests cover workflow decisions and generated hook context. Validate these repository-dependent recovery paths manually in a disposable repository before enabling autonomous mode: one local-only `codex/issue-<number>-*` branch, one remote-only branch, one branch present locally and remotely, zero matching branches, multiple matching branches, one open head-branch pull request, one closed-unmerged head-branch pull request, and multiple head-branch pull requests.

## Development

Clone and build the project:

```bash
git clone https://github.com/CagatayDilsiz/codex-github-router.git
cd codex-github-router
dotnet restore CodexGithubRouter.slnx
dotnet build CodexGithubRouter.slnx -c Release --no-restore
```

Run the CLI directly from source:

```bash
dotnet run --project src/CodexGithubRouter -- --help
```

Run the test suite with standard .NET test discovery and filtering. The commands below build the Release output as part of test execution; CI uses `--no-build` only after its preceding Release build step:

```bash
dotnet test CodexGithubRouter.slnx -c Release
dotnet test CodexGithubRouter.slnx -c Release --filter FullyQualifiedName~ConfigurationSandboxTests
dotnet test CodexGithubRouter.slnx -c Release --filter "Category=Unit"
dotnet test CodexGithubRouter.slnx -c Release --filter "Category=Integration"
```

The default suite runs both deterministic Unit tests and sandboxed Integration tests. It does not require GitHub, a network connection, or a live user configuration. Filesystem integration tests use unique temporary sandboxes; normal CLI execution continues to use the user-level `.codex` and `.codex-github-router` directories.

Create a local tool package:

```bash
dotnet pack src/CodexGithubRouter/CodexGithubRouter.csproj -c Release
```

Install the locally packed tool:

```bash
dotnet tool install --global \
  --add-source ./src/CodexGithubRouter/nupkg \
  codex-github-router \
  --version 0.0.2-alpha
```

## License

Codex GitHub Router is licensed under the MIT License.
