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

Autonomous mode is repository-specific. When it is enabled, the Codex hook can route prompts according to the configured GitHub issue and pull-request workflow. `cgr auto on` validates the workflow configuration and creates only missing labels referenced by its issue and pull-request label rules; existing labels are never changed. CGR stores the applied configuration fingerprint in the repository's shared Git directory so the same setup also works from Git worktrees. After changing the workflow configuration, run `cgr auto on` again to provision any newly required labels safely.

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
dotnet restore
dotnet build
```

Run the CLI directly from source:

```bash
dotnet run --project src/CodexGithubRouter -- --help
```

Run the test suite with standard .NET test discovery and filtering:

```bash
dotnet test
dotnet test --filter FullyQualifiedName~ConfigurationSandboxTests
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Integration"
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
  --version 0.0.1-alpha
```

## License

Codex GitHub Router is licensed under the MIT License.
