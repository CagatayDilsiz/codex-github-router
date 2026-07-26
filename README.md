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

Autonomous mode is repository-specific. When it is enabled, the Codex hook can route prompts according to the configured GitHub issue and pull-request workflow. `cgr auto on` validates the workflow configuration and creates only missing labels referenced by its issue and pull-request label rules; existing labels are never changed. CGR stores the applied configuration fingerprint in the repository's shared Git directory so the same setup also works from Git worktrees and later configuration changes trigger another safe provisioning pass.

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
