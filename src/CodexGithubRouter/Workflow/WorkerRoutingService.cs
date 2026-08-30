using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Workflow;

public static class WorkerRoutingService
{
    public const string WorkerLabelPrefix = "codex:worker:";

    public static bool IsEnabled(RouterConfiguration configuration) =>
        configuration.Policies?.WorkerRouting is not null;

    public static IReadOnlyList<string> GetLabels(RouterConfiguration configuration) =>
        configuration.Policies?.WorkerRouting?.Workers
            .SelectMany(worker => worker.Value.Labels)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

    public static string? ResolveWorkerForModel(RouterConfiguration configuration, string? model)
    {
        if (!IsEnabled(configuration) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return FindWorkerForModel(configuration.Policies!.WorkerRouting!, model);
    }

    public static void Validate(RouterConfiguration configuration)
    {
        var policy = configuration.Policies?.WorkerRouting;
        if (policy is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(policy.DefaultWorker))
        {
            throw new InvalidOperationException("Worker routing requires a default worker.");
        }

        if (!string.Equals(policy.DefaultWorker, policy.DefaultWorker.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Worker routing default worker must not have leading or trailing whitespace.");
        }

        if (policy.Workers is null || policy.Workers.Count == 0)
        {
            throw new InvalidOperationException("Worker routing requires at least one worker profile.");
        }

        var workerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in policy.Workers)
        {
            var workerName = entry.Key;
            var profile = entry.Value ?? throw new InvalidOperationException($"Worker profile '{workerName}' cannot be null.");
            if (string.IsNullOrWhiteSpace(workerName))
            {
                throw new InvalidOperationException("Worker profile names must not be empty.");
            }

            if (!string.Equals(workerName, workerName.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Worker profile name '{workerName}' must not have leading or trailing whitespace.");
            }

            if (!workerNames.Add(workerName))
            {
                throw new InvalidOperationException($"Worker profile '{workerName}' is configured more than once.");
            }

            if (profile.Labels is null || profile.Labels.Count == 0)
            {
                throw new InvalidOperationException($"Worker profile '{workerName}' requires at least one label.");
            }

            if (profile.Models is null || profile.Models.Count == 0)
            {
                throw new InvalidOperationException($"Worker profile '{workerName}' requires at least one model.");
            }

            foreach (var label in profile.Labels)
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    throw new InvalidOperationException($"Worker profile '{workerName}' contains an empty label.");
                }

                if (!string.Equals(label, label.Trim(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Worker label '{label}' must not have leading or trailing whitespace.");
                }

                if (!label.StartsWith(WorkerLabelPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Worker label '{label}' must use the '{WorkerLabelPrefix}' namespace.");
                }

                if (!labels.TryAdd(label, workerName))
                {
                    throw new InvalidOperationException($"Worker label '{label}' is assigned to multiple worker profiles.");
                }
            }

            foreach (var model in profile.Models)
            {
                if (string.IsNullOrWhiteSpace(model))
                {
                    throw new InvalidOperationException($"Worker profile '{workerName}' contains an empty model.");
                }

                if (!string.Equals(model, model.Trim(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Worker model '{model}' must not have leading or trailing whitespace.");
                }

                if (!models.TryAdd(model, workerName))
                {
                    throw new InvalidOperationException($"Worker model '{model}' is assigned to multiple worker profiles.");
                }
            }
        }

        if (!workerNames.Contains(policy.DefaultWorker))
        {
            throw new InvalidOperationException($"Worker routing default worker '{policy.DefaultWorker}' is not configured.");
        }
    }

    public static WorkerEligibility Evaluate(RouterConfiguration configuration, Issue issue, string? currentModel)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(issue);

        var policy = configuration.Policies?.WorkerRouting;
        if (policy is null)
        {
            return WorkerEligibility.Disabled;
        }

        var resolution = ResolveIssueWorker(configuration, issue);
        var issueWorker = resolution.WorkerProfile;
        var currentWorker = FindWorkerForModel(policy, currentModel);
        if (!resolution.IsEligible)
        {
            return Ineligible(issueWorker, currentWorker, currentModel, resolution.Message);
        }

        if (string.IsNullOrWhiteSpace(currentModel))
        {
            return Ineligible(issueWorker, null, currentModel, "Worker routing is configured, but the hook payload did not include a model.");
        }

        if (currentWorker is null)
        {
            return Ineligible(issueWorker, null, currentModel, $"No worker profile accepts the current model '{currentModel}'.");
        }

        if (!string.Equals(issueWorker, currentWorker, StringComparison.OrdinalIgnoreCase))
        {
            return Ineligible(issueWorker, currentWorker, currentModel, $"Issue #{issue.Number} belongs to worker '{issueWorker}', but the current model resolves to worker '{currentWorker}'.");
        }

        return new WorkerEligibility
        {
            IsEnabled = true,
            IsEligible = true,
            WorkerProfile = issueWorker,
            CurrentWorkerProfile = currentWorker,
            Model = currentModel
        };
    }

    public static WorkerLabelResolution ResolveIssueWorker(RouterConfiguration configuration, Issue issue)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(issue);

        var policy = configuration.Policies?.WorkerRouting;
        if (policy is null)
        {
            return WorkerLabelResolution.Disabled;
        }

        var configuredLabels = GetConfiguredLabels(policy);
        var workerLabels = issue.Labels
            .Select(label => label.Name?.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .Where(label => label.StartsWith(WorkerLabelPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unknownLabels = workerLabels
            .Where(label => !configuredLabels.ContainsKey(label))
            .ToList();

        if (workerLabels.Count > 1)
        {
            return InvalidWorkerResolution($"Issue #{issue.Number} has conflicting worker labels: {string.Join(", ", workerLabels)}.");
        }

        if (unknownLabels.Count > 0)
        {
            return InvalidWorkerResolution($"Issue #{issue.Number} has unknown worker label(s): {string.Join(", ", unknownLabels)}.");
        }

        var worker = workerLabels.Count == 0
            ? policy.DefaultWorker.Trim()
            : configuredLabels[workerLabels[0]];

        return new WorkerLabelResolution
        {
            IsEnabled = true,
            IsEligible = true,
            WorkerProfile = worker
        };
    }

    public const int MaxDiscoveryScanLimit = 512;

    public static async Task<WorkerCandidateDiscoveryResult> DiscoverCandidatesAsync(
        RouterConfiguration configuration,
        int scanLimit,
        string? currentModel,
        Func<int, Task<IReadOnlyList<Issue>>> fetchIssues,
        AssignmentIdentity? assignmentIdentity = null,
        Func<int, Task<IReadOnlyList<Issue>>>? fetchPreferredIssues = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fetchIssues);
        if (scanLimit <= 0) throw new ArgumentOutOfRangeException(nameof(scanLimit));

        if (fetchPreferredIssues is not null && IsPreferredPhaseActive(configuration, assignmentIdentity))
        {
            var preferredIssues = (await fetchPreferredIssues(scanLimit)).ToList();
            if (preferredIssues.Count > 0)
            {
                var preferredFilter = FilterIssues(configuration, preferredIssues, currentModel, assignmentIdentity);
                var hasPreferredCandidate = preferredFilter.EligibleIssues.Any(issue => AssignmentRoutingService.IsPreferredCandidate(configuration, assignmentIdentity, issue));
                if (hasPreferredCandidate)
                {
                    return new WorkerCandidateDiscoveryResult(preferredIssues, preferredFilter.IneligibleIssues);
                }
            }
        }

        var requestedLimit = scanLimit;
        var issues = new Dictionary<int, Issue>();
        var previousWindowCount = -1;

        while (true)
        {
            var window = await fetchIssues(requestedLimit);
            foreach (var issue in window)
            {
                issues[issue.Number] = issue;
            }

            if (!IsEnabled(configuration) && !AssignmentRoutingService.IsEnabled(configuration))
            {
                break;
            }

            var current = issues.Values.ToList();
            var eligibleCount = FilterIssues(configuration, current, currentModel, assignmentIdentity).EligibleIssues.Count;
            var scanExhausted = window.Count < requestedLimit || window.Count <= previousWindowCount || requestedLimit >= MaxDiscoveryScanLimit;
            if (eligibleCount >= scanLimit || scanExhausted)
            {
                break;
            }

            previousWindowCount = window.Count;
            requestedLimit = checked(requestedLimit * 2);
        }

        var discovered = issues.Values.ToList();
        var filter = FilterIssues(configuration, discovered, currentModel, assignmentIdentity);
        return new WorkerCandidateDiscoveryResult(discovered, filter.IneligibleIssues);
    }

    private static bool IsPreferredPhaseActive(RouterConfiguration configuration, AssignmentIdentity? assignmentIdentity) =>
        AssignmentRoutingService.IsPreferMode(configuration) && assignmentIdentity?.GitHubUsernames is { Count: > 0 };

    public static WorkflowResponse FilterCodingTasks(
        RouterConfiguration configuration,
        IReadOnlyList<Issue> issues,
        WorkflowResponse response,
        string? currentModel)
    {
        if (!IsEnabled(configuration) || response.Tasks.Count == 0)
        {
            return response;
        }

        var issueByNumber = issues.ToDictionary(issue => issue.Number);
        var eligibleTasks = new List<WorkflowItem>();
        var ineligible = new List<WorkerEligibility>();
        foreach (var task in response.Tasks)
        {
            if (task.Type is not (WorkflowItemType.ChangeRequest or WorkflowItemType.ResumeInProgressIssue or WorkflowItemType.NewIssue))
            {
                eligibleTasks.Add(task);
                continue;
            }

            if (!issueByNumber.TryGetValue(task.IssueNumber, out var issue))
            {
                eligibleTasks.Add(task);
                continue;
            }

            var eligibility = Evaluate(configuration, issue, currentModel);
            if (eligibility.IsEligible)
            {
                eligibleTasks.Add(task);
            }
            else
            {
                ineligible.Add(eligibility);
            }
        }

        var noEligibleWork = response.Tasks.Any(task => task.Type is WorkflowItemType.ChangeRequest or WorkflowItemType.ResumeInProgressIssue or WorkflowItemType.NewIssue) &&
            eligibleTasks.Count == 0 && ineligible.Count > 0;
        return new WorkflowResponse
        {
            Tasks = eligibleTasks,
            IsSuccessful = response.IsSuccessful,
            NoEligibleWork = noEligibleWork,
            IneligibleWorkerIssues = ineligible,
            Message = noEligibleWork ? FormatNoEligibleWorkMessage(currentModel, ineligible) : response.Message
        };
    }

    public static WorkerEligibility EvaluateClaim(RouterConfiguration configuration, WorkClaim claim, Issue issue, string? currentModel)
    {
        var eligibility = Evaluate(configuration, issue, currentModel);
        if (!eligibility.IsEligible || string.IsNullOrWhiteSpace(claim.WorkerProfile))
        {
            return eligibility;
        }

        if (!string.Equals(claim.WorkerProfile, eligibility.WorkerProfile, StringComparison.OrdinalIgnoreCase))
        {
            return Ineligible(eligibility.WorkerProfile, eligibility.CurrentWorkerProfile, currentModel, $"Active work claim for issue #{claim.IssueNumber} belongs to worker '{claim.WorkerProfile}', but the issue now resolves to worker '{eligibility.WorkerProfile}'.");
        }

        return eligibility;
    }

    public static WorkerIssueFilterResult FilterIssues(RouterConfiguration configuration, IReadOnlyList<Issue> issues, string? currentModel)
    {
        if (!IsEnabled(configuration))
        {
            return new WorkerIssueFilterResult(issues, Array.Empty<WorkerEligibility>(), false, string.Empty);
        }

        var eligible = new List<Issue>();
        var ineligible = new List<WorkerEligibility>();
        foreach (var issue in issues)
        {
            var eligibility = Evaluate(configuration, issue, currentModel);
            if (eligibility.IsEligible)
            {
                eligible.Add(issue);
            }
            else
            {
                ineligible.Add(eligibility);
            }
        }

        var message = FormatNoEligibleWorkMessage(currentModel, ineligible);
        return new WorkerIssueFilterResult(eligible, ineligible, issues.Count > 0 && eligible.Count == 0, message);
    }

    public static WorkerIssueFilterResult FilterIssues(RouterConfiguration configuration, IReadOnlyList<Issue> issues, string? currentModel, AssignmentIdentity? assignmentIdentity)
    {
        var workerFiltered = FilterIssues(configuration, issues, currentModel);
        if (!AssignmentRoutingService.IsEnabled(configuration))
        {
            return workerFiltered;
        }

        var assignmentFiltered = AssignmentRoutingService.FilterIssues(configuration, assignmentIdentity, workerFiltered.EligibleIssues);
        var noEligibleWork = issues.Count > 0 &&
            assignmentFiltered.EligibleIssues.Count == 0 &&
            (assignmentFiltered.IneligibleIssues.Count > 0 || workerFiltered.NoEligibleWork);
        var message = noEligibleWork
            ? CombineMessages(workerFiltered.Message, assignmentFiltered.Message)
            : string.Empty;
        return new WorkerIssueFilterResult(
            assignmentFiltered.EligibleIssues.ToList(),
            workerFiltered.IneligibleIssues,
            noEligibleWork,
            message);
    }

    private static string CombineMessages(params string?[] messages) =>
        string.Join(Environment.NewLine, messages.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.Ordinal));

    public static string FormatNoEligibleWorkMessage(string? currentModel, IReadOnlyList<WorkerEligibility> ineligible)
    {
        var profiles = ineligible
            .Select(result => result.WorkerProfile)
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var details = ineligible
            .Select(result => result.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        var lines = new List<string>
        {
            "No eligible work is available for the current worker.",
            $"Current model: {(string.IsNullOrWhiteSpace(currentModel) ? "<missing>" : currentModel)}"
        };

        var currentWorker = ineligible.Select(result => result.CurrentWorkerProfile).FirstOrDefault(profile => !string.IsNullOrWhiteSpace(profile));
        if (!string.IsNullOrWhiteSpace(currentWorker))
        {
            lines.Add($"Resolved worker: {currentWorker}");
        }

        if (profiles.Count > 0)
        {
            lines.Add($"Pending work exists for: {string.Join(", ", profiles)}");
        }

        lines.AddRange(details.Select(detail => $"- {detail}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static Dictionary<string, string> GetConfiguredLabels(WorkerRoutingPolicy policy)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var worker in policy.Workers)
        {
            foreach (var label in worker.Value.Labels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                {
                    labels[label.Trim()] = worker.Key.Trim();
                }
            }
        }

        return labels;
    }

    private static string? FindWorkerForModel(WorkerRoutingPolicy policy, string? model) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : policy.Workers.FirstOrDefault(worker => worker.Value.Models.Any(acceptedModel => string.Equals(acceptedModel, model.Trim(), StringComparison.Ordinal))).Key;

    private static WorkerLabelResolution InvalidWorkerResolution(string message) => new()
    {
        IsEnabled = true,
        IsEligible = false,
        Message = message
    };

    private static WorkerEligibility Ineligible(string? issueWorker, string? currentWorker, string? model, string message) => new()
    {
        IsEnabled = true,
        IsEligible = false,
        WorkerProfile = issueWorker,
        CurrentWorkerProfile = currentWorker,
        Model = model,
        Message = message
    };
}

public sealed class WorkerLabelResolution
{
    public static WorkerLabelResolution Disabled { get; } = new() { IsEligible = true };

    public bool IsEnabled { get; init; }
    public bool IsEligible { get; init; }
    public string? WorkerProfile { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record WorkerCandidateDiscoveryResult(
    IReadOnlyList<Issue> Issues,
    IReadOnlyList<WorkerEligibility> IneligibleIssues);

public sealed class WorkerEligibility
{
    public static WorkerEligibility Disabled { get; } = new() { IsEligible = true };

    public bool IsEnabled { get; init; }
    public bool IsEligible { get; init; }
    public string? WorkerProfile { get; init; }
    public string? CurrentWorkerProfile { get; init; }
    public string? Model { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record WorkerIssueFilterResult(
    IReadOnlyList<Issue> EligibleIssues,
    IReadOnlyList<WorkerEligibility> IneligibleIssues,
    bool NoEligibleWork,
    string Message);
