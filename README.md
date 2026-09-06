# Codex GitHub Router

> [!WARNING]
> This project is under active development and is currently in an alpha stage. Commands, configuration, and workflow behavior may change between releases.

Codex GitHub Router (`cgr`) is a small .NET CLI that connects [Codex](https://openai.com/index/introducing-codex/) sessions with GitHub Issues and Pull Requests. It installs a Codex `UserPromptSubmit` hook, finds the next actionable workflow item, prioritizes existing review or change-request work, and can prevent new work from starting while an earlier item still requires attention.

CGR turns a labeled GitHub workflow (ready → working → done) into router decisions. When you ask Codex to "work on the next task", the hook checks the repository state and delivers the additional context Codex needs: which issue to take, whether an in-progress issue must be resumed, whether a pull request is waiting on a change request, and so on. It keeps a repository-wide claim set with at most one active coding claim per worktree, so parallel work can proceed across Git worktrees while sessions agree on who owns which issue.

## Requirements

- .NET 10 runtime
- [GitHub CLI](https://cli.github.com/) installed and authenticated (`gh auth status`)
- Codex CLI with hooks support
- A GitHub repository that uses the configured issue and pull-request labels

## Installation

```bash
dotnet tool install --global codex-github-router --version 0.1.0-alpha
cgr --version
```

See [docs/getting-started.md](docs/getting-started.md) for the full install, update, and uninstall lifecycle.

## Quick start

```bash
# 1. Check GitHub authentication
gh auth status

# 2. Initialize the router (creates the default configuration and registers the Codex hook)
cgr init

# 3. Enable autonomous mode for your repository (provisions missing workflow, gate, and worker labels)
cgr auto on

# 4. Confirm the environment and repository are healthy
cgr doctor
```

After enabling autonomous mode, prompts submitted to Codex inside the repository are routed according to the workflow. Codex may need to be restarted after `cgr init` changes its hooks configuration. A five-minute walkthrough lives in [docs/getting-started.md](docs/getting-started.md).

## Basic usage

Run these commands from a Git repository connected to GitHub:

```bash
cgr --help
cgr issue list
cgr issue list --state InProgress
cgr pr list
cgr auto status
cgr work status
cgr work list
cgr work list --model gpt-5-codex
cgr explain
cgr explain --issue 12
cgr config validate
cgr doctor
```

`cgr work list` and `cgr explain` are strictly read-only. They run the same production routing scan as the hook (repository gate, then completed, in-progress, and ready discovery) and explain *why* each issue was eligible, blocked, or selected — including workflow state, candidate discovery, worker and assignment routing, repository-gate handling, the active work claim, and the final production routing decision. A claim owned by a worktree that no longer exists is excluded with the same stale-worktree evaluation production pruning uses, without writing, so a deleted worktree never occupies work in diagnostics. `cgr work list --model <model>` asks "what would this model route?" the same way `cgr explain --model <model>` does. Assignment identity is resolved by the same fail-closed plan stage the hook uses, and a claim that production reconciliation would release (blocked/needs-info/abandoned/closed/missing issue, or a missing/passive/terminal claimed pull request — including a passive pull request production would first associate with a claim that has no PR number yet) is reported as "would be released, ordinary routing continues" without ever modifying the claim file.

## Troubleshooting

**Start with `cgr doctor`.** It is strictly read-only and reports independent `PASS` / `WARN` / `FAIL` checks across the environment (version, .NET runtime, Git, GitHub CLI, hooks, global configuration) and the repository (override, effective configuration, autonomous mode, work claim, labels, worker routing, assignment routing). Exit codes: `0` for all-pass or warnings-only, `1` when any required setup fails, `2` for usage errors.

```bash
cgr doctor
cgr doctor --model gpt-5-codex
```

See [docs/troubleshooting.md](docs/troubleshooting.md) for step-by-step recovery guidance.

## How it works (lifecycle overview)

1. Codex submits a user prompt and the installed `cgr hook` fires on `UserPromptSubmit`.
2. CGR checks autonomous mode and the activation policy (always, or an exact prompt gate).
3. CGR reconciles the repository work claim, then routes the prompt:
   - an active claim owned by the current session continues that work,
   - a change-requested pull request is prioritized,
   - an in-progress issue is resumed before new work,
   - otherwise the next ready issue is claimed and started.
4. The hook returns additional context to Codex (or a `block` decision with a reason), and writes a diagnostic record for troubleshooting.

Workflow and pull-request labels, worker routing, assignee-aware routing, repository gates, and diagnostics are all configurable. See [docs/configuration.md](docs/configuration.md) and [docs/scenarios.md](docs/scenarios.md).

## Documentation

- [Getting started](docs/getting-started.md) — install, setup, uninstall lifecycle
- [Configuration reference](docs/configuration.md) — defaults, overrides, effective merge, validation
- [Scenarios](docs/scenarios.md) — common workflows with copyable examples
- [Troubleshooting](docs/troubleshooting.md) — doctor-first recovery guidance
- [Roadmap](docs/roadmap.md) — intended capabilities

## Current scope and limitations

- At most **one active coding claim per worktree**, with the full repository claim set shared across worktrees.
- CGR never starts a second issue, branch, or pull request in a worktree while that worktree has an active claim; different worktrees claim different work items. Work owned by another worktree is treated as occupied and skipped by routing and `cgr explain`, so each worktree routes the next item it can actually claim.
- Autonomous mode is repository-specific and stored in the shared Git common directory.
- The router relies on GitHub labels to model state; conflicting labels are treated as an ambiguous state and block the hook.
- PR review itself is not a claimable work type yet; change requests on linked pull requests are.

See [docs/roadmap.md](docs/roadmap.md) for the intended future capabilities.

## Development

```bash
dotnet restore CodexGithubRouter.slnx
dotnet build CodexGithubRouter.slnx -c Release --no-restore
dotnet run --project src/CodexGithubRouter -- --help
dotnet test CodexGithubRouter.slnx -c Release
```

The default test suite runs deterministic unit tests and sandboxed integration tests without requiring GitHub, a network connection, or a live user configuration. CI validates Release builds on Linux (`ubuntu-latest`) and Windows (`windows-latest`); macOS is not a required CI platform.

## Releases

Releases are published manually from the current `main` head through the **Release NuGet package** GitHub Actions workflow. NuGet publication uses Trusted Publishing with a short-lived OIDC-backed API key; the release environment can enforce approvals. The checked-in project version is `0.0.1-dev`; the release workflow supplies the resolved version so package metadata and `cgr --version` stay aligned. See [docs/getting-started.md](docs/getting-started.md) for the supported update and uninstall order.

## License

Codex GitHub Router is licensed under the MIT License.
