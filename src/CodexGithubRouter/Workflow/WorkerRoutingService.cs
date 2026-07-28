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

            if (!workerNames.Add(workerName.Trim()))
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

        var configuredLabels = GetConfiguredLabels(policy);
        var issueWorkerLabels = issue.Labels
            .Select(label => label.Name?.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .Where(label => configuredLabels.ContainsKey(label) || label.StartsWith(WorkerLabelPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unknownLabels = issueWorkerLabels
            .Where(label => !configuredLabels.ContainsKey(label))
            .ToList();

        string? issueWorker = null;
        if (issueWorkerLabels.Count == 0)
        {
            issueWorker = policy.DefaultWorker.Trim();
        }
        else if (issueWorkerLabels.Count == 1 && unknownLabels.Count == 0)
        {
            issueWorker = configuredLabels[issueWorkerLabels[0]];
        }

        var currentWorker = FindWorkerForModel(policy, currentModel);
        if (issueWorkerLabels.Count > 1)
        {
            return Ineligible(issueWorker, currentWorker, currentModel, $"Issue #{issue.Number} has conflicting worker labels: {string.Join(", ", issueWorkerLabels)}.");
        }

        if (unknownLabels.Count > 0)
        {
            return Ineligible(issueWorker, currentWorker, currentModel, $"Issue #{issue.Number} has unknown worker label(s): {string.Join(", ", unknownLabels)}.");
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
