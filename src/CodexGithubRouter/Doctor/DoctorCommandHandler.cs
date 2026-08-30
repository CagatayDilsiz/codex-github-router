using System.Text.Json;
using System.Text.Json.Nodes;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Helpers;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Doctor;

public static class DoctorCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, new DoctorCommandDependencies(), CancellationToken.None);

    public static async Task<int> HandleAsync(string[] args, DoctorCommandDependencies dependencies, CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(args, out var workingDirectory, out var model, out var usageError))
        {
            dependencies.Error.WriteLine(usageError);
            return 2;
        }

        var result = new DoctorResult { WorkingDirectory = workingDirectory };
        await RunChecksAsync(result, workingDirectory, model, dependencies, cancellationToken);

        PrintReport(result, dependencies.Output);

        return result.HasFailure ? 1 : 0;
    }

    private static bool TryParseArguments(string[] args, out string workingDirectory, out string? model, out string error)
    {
        workingDirectory = Environment.CurrentDirectory;
        model = null;
        error = string.Empty;

        var positionals = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--model", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    error = "cgr doctor: --model requires a value.";
                    return false;
                }

                model = args[++index];
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"cgr doctor: unknown option: {argument}";
                return false;
            }

            positionals.Add(argument);
        }

        if (positionals.Count > 1)
        {
            error = "cgr doctor: too many arguments.";
            return false;
        }

        if (positionals.Count == 1)
        {
            workingDirectory = positionals[0];
        }

        return true;
    }

    private static async Task RunChecksAsync(
        DoctorResult result,
        string workingDirectory,
        string? model,
        DoctorCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        result.Checks.Add(new DoctorCheck
        {
            Name = "CGR Version",
            Status = DoctorCheckStatus.Pass,
            Detail = "v" + VersionFormatter.GetVersion()
        });

        await AddDotNetRuntimeCheckAsync(result, dependencies, cancellationToken);
        await AddExecutableCheckAsync(result, "Git", "git", dependencies, cancellationToken);
        await AddExecutableCheckAsync(result, "GitHub CLI", "gh", dependencies, cancellationToken);
        await AddGitHubAuthenticationCheckAsync(result, dependencies, cancellationToken);
        await AddHooksConfigurationChecksAsync(result, dependencies, cancellationToken);
        await AddGlobalConfigurationCheckAsync(result, dependencies, cancellationToken);

        string? repositoryRoot = null;
        try
        {
            repositoryRoot = await dependencies.GetRepositoryRootAsync(workingDirectory, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Git Repository",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Could not inspect: {exception.Message}"
            });
        }

        if (repositoryRoot is null)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Git Repository",
                Status = DoctorCheckStatus.Failure,
                Detail = "Not a valid Git repository."
            });
            result.Checks.Add(new DoctorCheck
            {
                Name = "Repository Checks",
                Status = DoctorCheckStatus.Warning,
                Detail = "Skipped: run cgr doctor from within a Git repository."
            });
            return;
        }

        result.RepositoryRoot = repositoryRoot;
        result.Checks.Add(new DoctorCheck
        {
            Name = "Git Repository",
            Status = DoctorCheckStatus.Pass,
            Detail = repositoryRoot
        });

        var gitCommonDirectory = await ResolveGitCommonDirectoryAsync(result, workingDirectory, dependencies, cancellationToken);

        await AddRepositoryConfigurationCheckAsync(result, repositoryRoot, dependencies, cancellationToken);
        await AddEffectiveConfigurationCheckAsync(result, repositoryRoot, dependencies, cancellationToken);
        AddAutonomousModeCheck(result, gitCommonDirectory, dependencies);
        await AddActiveWorkClaimCheckAsync(result, gitCommonDirectory, dependencies, cancellationToken);
        await AddRequiredLabelsCheckAsync(result, repositoryRoot, dependencies, cancellationToken);
        await AddWorkerRoutingCheckAsync(result, model, dependencies, cancellationToken);
        await AddAssignmentRoutingCheckAsync(result, dependencies, cancellationToken);
    }

    private static async Task AddDotNetRuntimeCheckAsync(DoctorResult result, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        var versionProcess = await dependencies.RunVersionProcessAsync("dotnet", cancellationToken);
        if (versionProcess is null)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = ".NET Runtime",
                Status = DoctorCheckStatus.Failure,
                Detail = "'dotnet' could not be executed. Install the .NET SDK and ensure it is on the PATH."
            });
            return;
        }

        var runtimesProcess = await dependencies.RunDotNetRuntimesProcessAsync(cancellationToken);
        if (runtimesProcess is null || runtimesProcess.ExitCode != 0)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = ".NET Runtime",
                Status = DoctorCheckStatus.Warning,
                Detail = "'dotnet' is available but installed runtimes could not be determined."
            });
            return;
        }

        var supportedRuntime = ParseNetCoreAppVersions(runtimesProcess.Output)
            .OrderByDescending(version => version)
            .FirstOrDefault(version => version.Major == 10);
        if (supportedRuntime is null)
        {
            var foundVersions = string.Join(", ", ParseNetCoreAppVersions(runtimesProcess.Output).Select(version => version.ToString()).Distinct());
            result.Checks.Add(new DoctorCheck
            {
                Name = ".NET Runtime",
                Status = DoctorCheckStatus.Failure,
                Detail = string.IsNullOrWhiteSpace(foundVersions)
                    ? "No Microsoft.NETCore.App runtime found. This tool targets .NET 10."
                    : $"No .NET 10 runtime found (found: {foundVersions}). This tool targets .NET 10."
            });
            return;
        }

        result.Checks.Add(new DoctorCheck
        {
            Name = ".NET Runtime",
            Status = DoctorCheckStatus.Pass,
            Detail = $"Microsoft.NETCore.App {supportedRuntime} (supports net10.0)"
        });
    }

    private static IEnumerable<Version> ParseNetCoreAppVersions(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versionText = parts[1].Split(['-', '+'], 2)[0];
            if (Version.TryParse(versionText, out var version))
            {
                yield return version;
            }
        }
    }

    private static async Task AddExecutableCheckAsync(
        DoctorResult result,
        string name,
        string executable,
        DoctorCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var process = await dependencies.RunVersionProcessAsync(executable, cancellationToken);
        if (process is null)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = name,
                Status = DoctorCheckStatus.Failure,
                Detail = $"'{executable}' could not be executed. Install it and ensure it is on the PATH."
            });
            return;
        }

        if (process.ExitCode != 0)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = name,
                Status = DoctorCheckStatus.Warning,
                Detail = $"'{executable}' reported exit code {process.ExitCode}."
            });
            return;
        }

        var version = process.Output.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        result.Checks.Add(new DoctorCheck
        {
            Name = name,
            Status = DoctorCheckStatus.Pass,
            Detail = string.IsNullOrWhiteSpace(version) ? "Available." : version
        });
    }

    private static async Task AddGitHubAuthenticationCheckAsync(DoctorResult result, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        var process = await dependencies.RunGitHubAuthStatusProcessAsync(cancellationToken);
        if (process is null)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "GitHub CLI Authentication",
                Status = DoctorCheckStatus.Warning,
                Detail = "Skipped: the GitHub CLI is not available."
            });
            return;
        }

        if (process.ExitCode != 0)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "GitHub CLI Authentication",
                Status = DoctorCheckStatus.Failure,
                Detail = "Not authenticated. Run 'gh auth login'."
            });
            return;
        }

        var account = process.Output.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("Logged in", StringComparison.OrdinalIgnoreCase));
        result.Checks.Add(new DoctorCheck
        {
            Name = "GitHub CLI Authentication",
            Status = DoctorCheckStatus.Pass,
            Detail = account ?? "Authenticated."
        });
    }

    private static async Task AddHooksConfigurationChecksAsync(DoctorResult result, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        var hooksFilePath = dependencies.Paths.CodexHooksFile;
        if (!dependencies.FileExists(hooksFilePath))
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Codex Hooks Configuration",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Not found: {hooksFilePath}. Run 'cgr init' to configure hooks."
            });
            result.Checks.Add(new DoctorCheck
            {
                Name = "CGR Hook Entry",
                Status = DoctorCheckStatus.Failure,
                Detail = "Not configured because the hooks file is missing. Run 'cgr init' to register the 'cgr hook' entry."
            });
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(await dependencies.ReadTextFileAsync(hooksFilePath, cancellationToken));
        }
        catch (JsonException)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Codex Hooks Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Not valid JSON: {hooksFilePath}."
            });
            return;
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Codex Hooks Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Could not read {hooksFilePath}: {exception.Message}."
            });
            return;
        }

        if (root is null)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Codex Hooks Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Empty or null: {hooksFilePath}."
            });
            return;
        }

        if (root is not JsonObject rootObject)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Codex Hooks Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Not a valid JSON object: {hooksFilePath}."
            });
            return;
        }

        result.Checks.Add(new DoctorCheck
        {
            Name = "Codex Hooks Configuration",
            Status = DoctorCheckStatus.Pass,
            Detail = hooksFilePath
        });

        var entryCount = CountCgrHookEntries(rootObject);
        if (entryCount == 0)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "CGR Hook Entry",
                Status = DoctorCheckStatus.Failure,
                Detail = "No 'cgr hook' entry found. Run 'cgr init' to register it."
            });
        }
        else if (entryCount == 1)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "CGR Hook Entry",
                Status = DoctorCheckStatus.Pass,
                Detail = "Exactly one 'cgr hook' entry is registered."
            });
        }
        else
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "CGR Hook Entry",
                Status = DoctorCheckStatus.Warning,
                Detail = $"{entryCount} 'cgr hook' entries found. Duplicates may run the hook multiple times. Run 'cgr hook uninstall' then 'cgr init' to restore a single entry."
            });
        }
    }

    private static async Task AddGlobalConfigurationCheckAsync(DoctorResult result, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        var workflowFile = dependencies.Paths.WorkflowFile;
        if (!dependencies.FileExists(workflowFile))
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Global Workflow Configuration",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Not found: {workflowFile}. Run 'cgr init' to create the default configuration."
            });
            return;
        }

        try
        {
            var configuration = await dependencies.LoadGlobalConfigurationAsync(cancellationToken);
            result.Checks.Add(new DoctorCheck
            {
                Name = "Global Workflow Configuration",
                Status = DoctorCheckStatus.Pass,
                Detail = $"{workflowFile} (version {configuration.Version})"
            });
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Global Workflow Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = exception.Message
            });
        }
    }

    private static async Task<string?> ResolveGitCommonDirectoryAsync(
        DoctorResult result,
        string workingDirectory,
        DoctorCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        try
        {
            var gitCommonDirectory = await dependencies.GetGitCommonDirectoryAsync(workingDirectory, cancellationToken);
            if (gitCommonDirectory is null)
            {
                result.Checks.Add(new DoctorCheck
                {
                    Name = "Git Common Directory",
                    Status = DoctorCheckStatus.Warning,
                    Detail = "Could not resolve the Git common directory."
                });
                return null;
            }

            result.Checks.Add(new DoctorCheck
            {
                Name = "Git Common Directory",
                Status = DoctorCheckStatus.Pass,
                Detail = gitCommonDirectory
            });
            return gitCommonDirectory;
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Git Common Directory",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Could not inspect: {exception.Message}"
            });
            return null;
        }
    }

    private static async Task AddRepositoryConfigurationCheckAsync(DoctorResult result, string repositoryRoot, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        var repositoryConfigPath = Path.Combine(repositoryRoot, ".codex-github-router", "workflow.json");
        if (!dependencies.FileExists(repositoryConfigPath))
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Repository Workflow Configuration",
                Status = DoctorCheckStatus.Pass,
                Detail = "No repository override; the global configuration applies."
            });
            return;
        }

        try
        {
            _ = JsonNode.Parse(await dependencies.ReadTextFileAsync(repositoryConfigPath, cancellationToken));
            result.Checks.Add(new DoctorCheck
            {
                Name = "Repository Workflow Configuration",
                Status = DoctorCheckStatus.Pass,
                Detail = repositoryConfigPath
            });
        }
        catch (JsonException)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Repository Workflow Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Not valid JSON: {repositoryConfigPath}."
            });
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Repository Workflow Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Could not read {repositoryConfigPath}: {exception.Message}."
            });
        }
    }

    private static async Task AddEffectiveConfigurationCheckAsync(DoctorResult result, string repositoryRoot, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        RouterConfiguration configuration;
        try
        {
            configuration = await dependencies.LoadEffectiveConfigurationAsync(repositoryRoot, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Effective Workflow Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = exception.Message
            });
            return;
        }

        var validationError = WorkflowConfigurationService.ValidateConfiguration(configuration);
        result.Checks.Add(validationError is null
            ? new DoctorCheck
            {
                Name = "Effective Workflow Configuration",
                Status = DoctorCheckStatus.Pass,
                Detail = $"Configuration is valid (version {configuration.Version})."
            }
            : new DoctorCheck
            {
                Name = "Effective Workflow Configuration",
                Status = DoctorCheckStatus.Failure,
                Detail = $"Configuration is invalid: {validationError}"
            });
    }

    private static void AddAutonomousModeCheck(DoctorResult result, string? gitCommonDirectory, DoctorCommandDependencies dependencies)
    {
        if (gitCommonDirectory is null)
        {
            return;
        }

        var markerPath = Path.Combine(gitCommonDirectory, "codex-github-router.auto");
        var isAutonomous = dependencies.FileExists(markerPath);
        result.Checks.Add(new DoctorCheck
        {
            Name = "Autonomous Mode",
            Status = DoctorCheckStatus.Pass,
            Detail = isAutonomous
                ? "Enabled. Repository automation is active."
                : "Disabled. Run 'cgr auto on' to enable autonomous issue processing."
        });
    }

    private static async Task AddActiveWorkClaimCheckAsync(DoctorResult result, string? gitCommonDirectory, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        if (gitCommonDirectory is null)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Active Work Claim",
                Status = DoctorCheckStatus.Warning,
                Detail = "Skipped: could not resolve the Git common directory."
            });
            return;
        }

        try
        {
            var claim = await dependencies.ReadWorkClaimAsync(gitCommonDirectory, cancellationToken);
            result.Checks.Add(claim is null
                ? new DoctorCheck
                {
                    Name = "Active Work Claim",
                    Status = DoctorCheckStatus.Pass,
                    Detail = "No active work claim."
                }
                : new DoctorCheck
                {
                    Name = "Active Work Claim",
                    Status = DoctorCheckStatus.Pass,
                    Detail = FormatClaimSummary(claim)
                });
        }
        catch (WorkClaimFileException exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Active Work Claim",
                Status = DoctorCheckStatus.Failure,
                Detail = $"{exception.Message} Repair or remove the work-claim file after confirming no active session owns the work."
            });
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Active Work Claim",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Could not inspect: {exception.Message}"
            });
        }
    }

    private static async Task AddRequiredLabelsCheckAsync(DoctorResult result, string repositoryRoot, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> requiredLabels;
        try
        {
            var configuration = await dependencies.LoadEffectiveConfigurationAsync(repositoryRoot, cancellationToken);
            requiredLabels = WorkflowLabelConfiguration.GetRequiredLabels(configuration);
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Required GitHub Labels",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Skipped: {exception.Message}"
            });
            return;
        }

        if (requiredLabels.Count == 0)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Required GitHub Labels",
                Status = DoctorCheckStatus.Pass,
                Detail = "No labels are required by the configuration."
            });
            return;
        }

        try
        {
            var existingLabels = await dependencies.GetRepositoryLabelNamesAsync(repositoryRoot, cancellationToken);
            var missingLabels = requiredLabels.Where(label => !existingLabels.Contains(label)).ToList();
            result.Checks.Add(missingLabels.Count == 0
                ? new DoctorCheck
                {
                    Name = "Required GitHub Labels",
                    Status = DoctorCheckStatus.Pass,
                    Detail = $"All {requiredLabels.Count} required label(s) exist in the repository."
                }
                : new DoctorCheck
                {
                    Name = "Required GitHub Labels",
                    Status = DoctorCheckStatus.Warning,
                    Detail = $"Missing label(s): {string.Join(", ", missingLabels)}. Run 'cgr auto on' to create them."
                });
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Required GitHub Labels",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Could not verify against GitHub: {exception.Message}"
            });
        }
    }

    private static async Task AddWorkerRoutingCheckAsync(DoctorResult result, string? model, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        RouterConfiguration configuration;
        try
        {
            configuration = await dependencies.LoadEffectiveConfigurationAsync(result.RepositoryRoot!, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Worker Routing",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Skipped: {exception.Message}"
            });
            return;
        }

        if (!WorkerRoutingService.IsEnabled(configuration))
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Worker Routing",
                Status = DoctorCheckStatus.Pass,
                Detail = "Disabled; the default worker resolution is not active."
            });
            return;
        }

        var policy = configuration.Policies!.WorkerRouting!;
        var detail = $"Default worker: '{policy.DefaultWorker}'. Configured workers: {string.Join(", ", policy.Workers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}.";

        if (string.IsNullOrWhiteSpace(model))
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Worker Routing",
                Status = DoctorCheckStatus.Pass,
                Detail = detail
            });
            return;
        }

        var worker = WorkerRoutingService.ResolveWorkerForModel(configuration, model);
        detail += $" Current model '{model}' resolves to worker '{(worker ?? "<none>")}'.";
        result.Checks.Add(new DoctorCheck
        {
            Name = "Worker Routing",
            Status = worker is null ? DoctorCheckStatus.Warning : DoctorCheckStatus.Pass,
            Detail = detail
        });
    }

    private static async Task AddAssignmentRoutingCheckAsync(DoctorResult result, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        RouterConfiguration configuration;
        try
        {
            configuration = await dependencies.LoadEffectiveConfigurationAsync(result.RepositoryRoot!, cancellationToken);
        }
        catch (Exception exception)
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Assignment Routing",
                Status = DoctorCheckStatus.Warning,
                Detail = $"Skipped: {exception.Message}"
            });
            return;
        }

        if (!AssignmentRoutingService.IsEnabled(configuration))
        {
            result.Checks.Add(new DoctorCheck
            {
                Name = "Assignment Routing",
                Status = DoctorCheckStatus.Pass,
                Detail = "Disabled; assignment-aware routing is not active."
            });
            return;
        }

        var policy = configuration.Policies!.AssignmentRouting!;
        var mode = AssignmentRoutingService.GetMode(configuration);
        var unassigned = AssignmentRoutingService.GetUnassignedMode(configuration);
        var detail = $"Mode: '{mode}'. Unassigned policy: '{unassigned}'.";
        var status = DoctorCheckStatus.Pass;

        if (AssignmentRoutingService.RequiresLocalIdentity(configuration))
        {
            var (usernames, source) = await ResolveIdentitySourcesAsync(result.RepositoryRoot!, dependencies, cancellationToken);
            var resolution = AssignmentRoutingService.Resolve(configuration, usernames);
            detail += $" Local identity: '{string.Join(", ", usernames)}' ({source}).";
            if (!resolution.IsResolved)
            {
                status = DoctorCheckStatus.Warning;
                detail += $" {resolution.Message}";
            }
        }

        result.Checks.Add(new DoctorCheck
        {
            Name = "Assignment Routing",
            Status = status,
            Detail = detail
        });
    }

    private static async Task<(IReadOnlyList<string> Usernames, string Source)> ResolveIdentitySourcesAsync(string repositoryRoot, DoctorCommandDependencies dependencies, CancellationToken cancellationToken)
    {
        var gitIdentityValue = await dependencies.ResolveLocalIdentityAsync(repositoryRoot, cancellationToken);
        var gitUsernames = AssignmentRoutingService.ParseIdentityUsernames(gitIdentityValue);
        if (gitUsernames.Count > 0)
        {
            return (gitUsernames, $"Git config key '{AssignmentRoutingService.LocalIdentityConfigKey}'");
        }

        string? authenticatedLogin = null;
        try
        {
            authenticatedLogin = await dependencies.ResolveAuthenticatedGitHubLoginAsync(repositoryRoot, cancellationToken);
        }
        catch
        {
            // A missing or failing GitHub CLI is reported as an unresolved identity.
        }

        if (!string.IsNullOrWhiteSpace(authenticatedLogin))
        {
            return (new[] { authenticatedLogin.Trim() }, "authenticated GitHub CLI account");
        }

        return (Array.Empty<string>(), "no source");
    }

    private static int CountCgrHookEntries(JsonObject root)
    {
        if (root["hooks"] is not JsonObject rootHooks)
        {
            return 0;
        }

        if (rootHooks["UserPromptSubmit"] is not JsonArray userPrompt)
        {
            return 0;
        }

        return userPrompt
            .OfType<JsonObject>()
            .Select(group => group["hooks"] as JsonArray)
            .Where(hooks => hooks is not null)
            .SelectMany(hooks => hooks!.OfType<JsonObject>())
            .Count(ConfigurationInitializer.IsCgrCommandBlock);
    }

    public static string FormatClaimSummary(WorkClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var metadata = new[]
        {
            string.IsNullOrWhiteSpace(claim.WorkerProfile) ? null : $"worker '{claim.WorkerProfile}'",
            string.IsNullOrWhiteSpace(claim.Model) ? null : $"model '{claim.Model}'"
        };
        var metadataSummary = string.Join(", ", metadata.Where(value => value is not null));

        var workIdentity = $"issue #{claim.IssueNumber}{(claim.PullRequestNumber.HasValue ? $" / pull request #{claim.PullRequestNumber.Value}" : string.Empty)}";
        var suffix = string.IsNullOrWhiteSpace(metadataSummary) ? string.Empty : $" ({metadataSummary})";
        return $"Active: {workIdentity}, {claim.WorkType}{suffix}.";
    }

    private static void PrintReport(DoctorResult result, TextWriter output)
    {
        output.WriteLine("CGR Doctor report");
        output.WriteLine();
        output.WriteLine($"Working directory: {result.WorkingDirectory}");
        if (result.RepositoryRoot is not null)
        {
            output.WriteLine($"Repository root: {result.RepositoryRoot}");
        }

        output.WriteLine();
        output.WriteLine("Checks:");
        foreach (var check in result.Checks)
        {
            var status = check.Status switch
            {
                DoctorCheckStatus.Pass => "PASS",
                DoctorCheckStatus.Warning => "WARN",
                _ => "FAIL"
            };
            var detail = string.IsNullOrWhiteSpace(check.Detail) ? string.Empty : $": {check.Detail}";
            output.WriteLine($"  [{status}] {check.Name}{detail}");
        }

        output.WriteLine();
        output.WriteLine($"Summary: {result.PassCount} passed, {result.WarningCount} warning(s), {result.FailureCount} failed.");

        var recommendations = BuildRecommendations(result);
        if (recommendations.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Recommendations:");
            foreach (var recommendation in recommendations)
            {
                output.WriteLine($"  - {recommendation}");
            }
        }
    }

    private static List<string> BuildRecommendations(DoctorResult result)
    {
        var recommendations = new List<string>();

        var failed = result.Checks.Where(check => check.Status == DoctorCheckStatus.Failure).Select(check => check.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warned = result.Checks.Where(check => check.Status == DoctorCheckStatus.Warning).Select(check => check.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (failed.Contains(".NET Runtime"))
        {
            recommendations.Add("Install the .NET runtime and ensure 'dotnet' is on the PATH.");
        }

        if (failed.Contains("Git"))
        {
            recommendations.Add("Install Git and ensure 'git' is on the PATH.");
        }

        if (failed.Contains("GitHub CLI") || failed.Contains("GitHub CLI Authentication"))
        {
            recommendations.Add("Install and authenticate the GitHub CLI with 'gh auth login'.");
        }

        if (failed.Contains("Codex Hooks Configuration") || warned.Contains("Codex Hooks Configuration") || failed.Contains("CGR Hook Entry") || warned.Contains("CGR Hook Entry"))
        {
            recommendations.Add("Run 'cgr init' to configure the Codex hooks and the default global configuration.");
        }

        if (failed.Contains("Global Workflow Configuration") || warned.Contains("Global Workflow Configuration") ||
            failed.Contains("Effective Workflow Configuration"))
        {
            recommendations.Add("Fix the workflow configuration so it validates; run 'cgr config validate' for details.");
        }

        if (failed.Contains("Repository Workflow Configuration"))
        {
            recommendations.Add("Fix the repository override at .codex-github-router/workflow.json.");
        }

        if (failed.Contains("Active Work Claim"))
        {
            recommendations.Add("Repair or remove the work-claim file after confirming no active session owns the work.");
        }

        if (failed.Contains("Git Repository"))
        {
            recommendations.Add("Run 'cgr doctor' from within a Git repository to run repository-level checks.");
        }

        if (warned.Contains("Required GitHub Labels"))
        {
            recommendations.Add("Run 'cgr auto on' inside the repository to create missing labels.");
        }

        return recommendations;
    }
}

public sealed class DoctorCommandDependencies
{
    private static ConfigurationPathSet DefaultPaths => ConfigurationPaths.Default;

    public ConfigurationPathSet Paths { get; init; } = ConfigurationPaths.Default;

    public TextWriter Output { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    public Func<string, CancellationToken, Task<string?>> GetRepositoryRootAsync { get; init; }
        = (workingDirectory, cancellationToken) => GitRepositoryService.GetRepositoryRootAsync(workingDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> GetGitCommonDirectoryAsync { get; init; }
        = (workingDirectory, cancellationToken) => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<ProcessResult?>> RunVersionProcessAsync { get; init; }
        = async (executable, cancellationToken) =>
        {
            try
            {
                return await ProcessRunner.RunAsync(Environment.CurrentDirectory, executable, new[] { "--version" }, cancellationToken);
            }
            catch
            {
                return null;
            }
        };

    public Func<CancellationToken, Task<ProcessResult?>> RunDotNetRuntimesProcessAsync { get; init; }
        = async cancellationToken =>
        {
            try
            {
                return await ProcessRunner.RunAsync(Environment.CurrentDirectory, "dotnet", new[] { "--list-runtimes" }, cancellationToken);
            }
            catch
            {
                return null;
            }
        };

    public Func<CancellationToken, Task<ProcessResult?>> RunGitHubAuthStatusProcessAsync { get; init; }
        = async cancellationToken =>
        {
            try
            {
                return await ProcessRunner.RunAsync(Environment.CurrentDirectory, "gh", new[] { "auth", "status" }, cancellationToken);
            }
            catch
            {
                return null;
            }
        };

    public Func<string, bool> FileExists { get; init; }
        = path => File.Exists(path);

    public Func<string, CancellationToken, Task<string>> ReadTextFileAsync { get; init; }
        = (path, cancellationToken) => File.ReadAllTextAsync(path, cancellationToken);

    public Func<CancellationToken, Task<RouterConfiguration>> LoadGlobalConfigurationAsync { get; init; }
        = cancellationToken => WorkflowConfigurationService.LoadOrDefaultAsync(DefaultPaths, cancellationToken);

    public Func<string, CancellationToken, Task<RouterConfiguration>> LoadEffectiveConfigurationAsync { get; init; }
        = (repositoryRoot, cancellationToken) => WorkflowConfigurationService.LoadEffectiveFromRepositoryRootAsync(repositoryRoot, DefaultPaths, cancellationToken);

    public Func<string, CancellationToken, Task<WorkClaim?>> ReadWorkClaimAsync { get; init; }
        = (gitCommonDirectory, cancellationToken) => WorkClaimStore.TryReadAsync(gitCommonDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<HashSet<string>>> GetRepositoryLabelNamesAsync { get; init; }
        = (repositoryRoot, cancellationToken) => GitHubCliService.GetRepositoryLabelNamesAsync(repositoryRoot, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> ResolveLocalIdentityAsync { get; init; }
        = (repositoryRoot, cancellationToken) => GitRepositoryService.GetConfigValueAsync(repositoryRoot, AssignmentRoutingService.LocalIdentityConfigKey, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> ResolveAuthenticatedGitHubLoginAsync { get; init; }
        = (repositoryRoot, cancellationToken) => GitHubCliService.GetAuthenticatedUserAsync(repositoryRoot, cancellationToken);
}
