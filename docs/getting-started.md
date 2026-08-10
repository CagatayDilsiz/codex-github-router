# Getting started

This guide covers installing Codex GitHub Router (`cgr`), running your first five-minute setup, and the supported install → update → uninstall lifecycle.

## Requirements

- **.NET 10** runtime. Verify with `dotnet --list-runtimes` (a `Microsoft.NETCore.App` runtime with major version `10` must be present).
- **GitHub CLI** installed and authenticated:
  ```bash
  gh auth status
  ```
  If it is not authenticated, run `gh auth login`.
- **Codex CLI** with hooks support.
- A **GitHub repository** you are allowed to label and branch.

## Install

Install the global .NET tool:

```bash
dotnet tool install --global codex-github-router --version 0.1.0-alpha
```

Verify the installation:

```bash
cgr --version
```

> NuGet is the primary installation channel. GitHub Releases retain the matching package, checksum, and release notes.

## Five-minute quick start

### 1. Initialize the router

```bash
cgr init
```

`cgr init` does two things:

- writes the default workflow configuration to `~/.codex-github-router/workflow.json`, and
- adds a single `cgr hook` command to the user-level Codex hooks file at `~/.codex/hooks.json`.

Existing hooks are preserved. If the hooks file already contains a `cgr hook` entry, `cgr init` reports that and does nothing; use `--force` to rewrite the generated configuration and refresh the hook entry:

```bash
cgr init --force
```

Codex may need to be restarted after its hooks configuration changes.

### 2. Check the environment

```bash
cgr doctor
```

`cgr doctor` is the first troubleshooting step. It is strictly read-only and reports independent checks:

- user level: CGR version, .NET runtime, Git, GitHub CLI availability and authentication, the Codex hooks file and the registered `cgr hook` entry, and the global workflow configuration;
- repository level: valid Git repository and common directory, the repository override, the effective workflow configuration, autonomous mode, the active work claim, required labels, and worker routing.

Each check is `PASS`, `WARN`, or `FAIL`. Exit codes: `0` all-pass/warnings-only, `1` any required setup failed, `2` usage error. A failing or warning check includes an actionable recommendation (for example `cgr init`, `gh auth login`, or `cgr auto on`).

### 3. Enable autonomous mode

From inside the repository:

```bash
cgr auto on
```

Autonomous mode is repository-specific. Enabling it validates the workflow configuration and provisions only the missing labels referenced by the configured issue and pull-request rules; existing labels are never changed. The applied configuration fingerprint is stored in the repository's shared Git directory so the same setup works from Git worktrees.

Check the state:

```bash
cgr auto status
```

### 4. Use it

Submit a prompt to Codex inside the repository, for example:

> work on the next task

The hook routes the prompt according to the workflow. See [scenarios.md](scenarios.md) for the supported workflows and [configuration.md](configuration.md) for activation modes.

## Inspecting and validating configuration

The `config` commands are read-only: they never create a missing configuration file.

```bash
cgr config path                       # show global config path (and repository override path when present)
cgr config show                       # show the stored global configuration
cgr config show --effective           # show the effective (merged) configuration
cgr config validate                   # validate the effective configuration
```

`config validate` reports `Configuration is valid.` and exits `0`, or reports an invalid configuration and exits `1`. See [configuration.md](configuration.md) for the full reference.

## Update

Update the tool to a specific prerelease:

```bash
dotnet tool update --global codex-github-router --version 0.1.0-alpha
```

After updating, run `cgr init --force` if the hooks entry format changed and `cgr doctor` to confirm everything is healthy. Repository-scoped state (autonomous markers, work claims, branches, working files) is intentionally left untouched by tool updates.

## Uninstall

Follow this order to remove CGR cleanly:

1. **Remove the Codex hook entries first** — otherwise Codex keeps trying to invoke a tool that no longer exists:
   ```bash
   cgr hook uninstall
   ```
2. **Remove the global .NET tool:**
   ```bash
   dotnet tool uninstall --global codex-github-router
   ```

### What `cgr hook uninstall` removes

- Only recognized CGR hook entries from the Codex hooks file (`~/.codex/hooks.json`).
- Both the Unix `command` entry and the Windows `commandWindows` entry are handled safely; a CGR entry is recognized when either its `command` or `commandWindows` value is `cgr hook` (when both are present, both must match).

### What it intentionally leaves untouched

- Other hook groups and unrelated handlers are preserved.
- Repository-scoped autonomous markers, work claims, branches, and working files are **not** removed. Cleaning up repository state is an explicit, separate action — see [troubleshooting.md](troubleshooting.md) before deleting anything.

### Repeating uninstall

Running `cgr hook uninstall` again after no CGR hook remains is safe: it reports that nothing was found and exits `0`.

## Where things live

| Item | Location |
| --- | --- |
| Global workflow configuration | `~/.codex-github-router/workflow.json` |
| Codex hooks file | `~/.codex/hooks.json` |
| Repository override | `<repo>/.codex-github-router/workflow.json` |
| Autonomous marker | `<repo>/.git/codex-github-router.auto` |
| Active work claim | `<repo>/.git/codex-github-router.work.json` |
| Hook diagnostics | `<repo>/.git/codex-github-router.diagnostics/` |

Paths resolve from the user profile; on Windows the `.codex` and `.codex-github-router` directories live under `%USERPROFILE%`, on Unix under `$HOME`. The repository-scoped files live in the **Git common directory** (`git rev-parse --git-common-dir`), which is shared by all worktrees.
