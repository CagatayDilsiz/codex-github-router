using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public static class AssignmentRoutingService
{
    public const string ModeIgnore = "ignore";
    public const string ModePrefer = "prefer";
    public const string ModeRequire = "require";
    public const string UnassignedAllow = "allow";
    public const string UnassignedExclude = "exclude";

    public const string LocalIdentityConfigKey = "codex-github-router.identity";

    public static bool IsEnabled(RouterConfiguration configuration) =>
        configuration.Policies?.AssignmentRouting is not null;

    public static bool RequiresLocalIdentity(RouterConfiguration configuration) =>
        IsEnabled(configuration) && !string.Equals(GetMode(configuration), ModeIgnore, StringComparison.Ordinal);

    public static string GetMode(RouterConfiguration configuration) =>
        IsEnabled(configuration) ? NormalizeMode(configuration.Policies!.AssignmentRouting!.Mode) : ModeIgnore;

    public static string GetUnassignedMode(RouterConfiguration configuration) =>
        IsEnabled(configuration) ? NormalizeUnassigned(configuration.Policies!.AssignmentRouting!.Unassigned) : UnassignedAllow;

    public static void Validate(RouterConfiguration configuration)
    {
        var policy = configuration.Policies?.AssignmentRouting;
        if (policy is null)
        {
            return;
        }

        if (!IsAllowedMode(policy.Mode))
        {
            throw new InvalidOperationException("Assignment routing mode must be one of: ignore, prefer, require.");
        }

        if (!IsAllowedUnassigned(policy.Unassigned))
        {
            throw new InvalidOperationException("Assignment routing unassigned policy must be one of: allow, exclude.");
        }
    }

    public static AssignmentIdentityResolution Resolve(RouterConfiguration configuration, IReadOnlyList<string>? localGitHubUsernames)
    {
        if (!IsEnabled(configuration) || !RequiresLocalIdentity(configuration))
        {
            return AssignmentIdentityResolution.NotEnabled;
        }

        var usernames = (localGitHubUsernames ?? Array.Empty<string>())
            .Select(username => username.Trim())
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (usernames.Count == 0)
        {
            return AssignmentIdentityResolution.Failure("Assignment routing is enabled, but the current identity could not be resolved: no CGR Git identity is configured and the authenticated GitHub account is unavailable.");
        }

        return new AssignmentIdentityResolution
        {
            IsEnabled = true,
            IsResolved = true,
            Identity = CreateIdentity(usernames.First(), usernames)
        };
    }

    public static IReadOnlyList<string> ParseIdentityUsernames(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<string>();
        }

        return rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AssignmentEligibility Evaluate(RouterConfiguration configuration, AssignmentIdentity? identity, Issue issue)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(issue);

        if (!IsEnabled(configuration))
        {
            return AssignmentEligibility.Disabled;
        }

        var mode = GetMode(configuration);
        var unassigned = GetUnassignedMode(configuration);
        var assignees = GetAssignees(issue);
        var isUnassigned = assignees.Count == 0;
        var assignedToCurrent = !isUnassigned && identity?.GitHubUsernames.Any(login => assignees.Contains(login, StringComparer.OrdinalIgnoreCase)) == true;

        if (mode == ModeIgnore)
        {
            return Eligible(identity, issue, 0, assignedToCurrent, isUnassigned);
        }

        if (identity?.GitHubUsernames is not { Count: > 0 })
        {
            return Ineligible(identity, issue, "Assignment routing is enabled, but the current identity could not be resolved.");
        }

        if (assignedToCurrent)
        {
            return Eligible(identity, issue, 0, true, isUnassigned);
        }

        if (isUnassigned)
        {
            if (unassigned == UnassignedAllow)
            {
                return Eligible(identity, issue, 1, false, true);
            }

            return Ineligible(identity, issue, $"Issue #{issue.Number} is unassigned and unassigned work is excluded by the assignment policy.");
        }

        if (mode == ModeRequire)
        {
            return Ineligible(identity, issue, $"Issue #{issue.Number} is assigned to developer(s) {string.Join(", ", assignees)} and assignment routing requires the current identity.");
        }

        return Eligible(identity, issue, 2, false, isUnassigned);
    }

    public static AssignmentIssueFilterResult FilterIssues(RouterConfiguration configuration, AssignmentIdentity? identity, IReadOnlyList<Issue> issues)
    {
        if (!IsEnabled(configuration))
        {
            return new AssignmentIssueFilterResult(issues, Array.Empty<AssignmentEligibility>(), false, string.Empty);
        }

        var eligible = new List<Issue>();
        var ineligible = new List<AssignmentEligibility>();
        foreach (var issue in issues)
        {
            var eligibility = Evaluate(configuration, identity, issue);
            if (eligibility.IsEligible)
            {
                eligible.Add(issue);
            }
            else
            {
                ineligible.Add(eligibility);
            }
        }

        var message = FormatNoEligibleWorkMessage(identity, ineligible);
        return new AssignmentIssueFilterResult(eligible, ineligible, issues.Count > 0 && eligible.Count == 0, message);
    }

    public static WorkflowResponse FilterCodingTasks(
        RouterConfiguration configuration,
        AssignmentIdentity? identity,
        IReadOnlyList<Issue> issues,
        WorkflowResponse response)
    {
        if (!IsEnabled(configuration) || response.Tasks.Count == 0)
        {
            return response;
        }

        var issueByNumber = issues.ToDictionary(issue => issue.Number);
        var eligibleTasks = new List<WorkflowItem>();
        var ineligible = new List<AssignmentEligibility>();
        foreach (var task in response.Tasks)
        {
            if (!IsIssueDerivedDeveloperWork(task.Type))
            {
                eligibleTasks.Add(task);
                continue;
            }

            if (!issueByNumber.TryGetValue(task.IssueNumber, out var issue))
            {
                eligibleTasks.Add(task);
                continue;
            }

            var eligibility = Evaluate(configuration, identity, issue);
            if (eligibility.IsEligible)
            {
                task.SelectionRank = eligibility.SelectionRank;
                eligibleTasks.Add(task);
            }
            else
            {
                ineligible.Add(eligibility);
            }
        }

        var noEligibleWork = (response.NoEligibleWork || (response.Tasks.Any(task => IsIssueDerivedDeveloperWork(task.Type)) &&
            eligibleTasks.Count == 0 && ineligible.Count > 0)) && response.Tasks.Count > 0;
        var message = noEligibleWork
            ? CombineMessages(response.Message, FormatNoEligibleWorkMessage(identity, ineligible))
            : response.Message;
        return new WorkflowResponse
        {
            Tasks = eligibleTasks,
            IsSuccessful = response.IsSuccessful,
            NoEligibleWork = noEligibleWork,
            IneligibleWorkerIssues = response.IneligibleWorkerIssues,
            IneligibleAssignmentIssues = ineligible,
            Message = message
        };
    }

    private static string CombineMessages(params string?[] messages) =>
        string.Join(Environment.NewLine, messages.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct(StringComparer.Ordinal));

    public static string FormatNoEligibleWorkMessage(AssignmentIdentity? identity, IReadOnlyList<AssignmentEligibility> ineligible)
    {
        var identityName = string.IsNullOrWhiteSpace(identity?.Name) ? "<missing>" : identity!.Name;
        var details = ineligible
            .Select(result => result.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        var lines = new List<string>
        {
            "No eligible work is available for the current identity.",
            $"Current identity: {identityName}"
        };

        var assigneeProfile = identity?.GitHubUsernames is { Count: > 0 } ? string.Join(", ", identity!.GitHubUsernames) : "<none>";
        lines.Add($"GitHub assignee(s): {assigneeProfile}");
        lines.AddRange(details.Select(detail => $"- {detail}"));
        return string.Join(Environment.NewLine, lines);
    }

    public static AssignmentIdentity? ResolveIdentity(AssignmentIdentityResolution resolution) =>
        resolution.IsEnabled && resolution.IsResolved ? resolution.Identity : null;

    private static AssignmentIdentity CreateIdentity(string name, IEnumerable<string> usernames) => new()
    {
        Name = name,
        GitHubUsernames = usernames
            .Select(username => username.Trim())
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(username => username, StringComparer.OrdinalIgnoreCase)
            .ToList()
    };

    private static List<string> GetAssignees(Issue issue) => (issue.Assignees ?? new List<GithubUser>())
        .Select(assignee => assignee.Login?.Trim())
        .Where(login => !string.IsNullOrWhiteSpace(login))
        .Select(login => login!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static bool IsPreferredScanComplete(RouterConfiguration configuration, AssignmentIdentity? identity, IReadOnlyList<Issue> issues, int requiredPreferredCount)
    {
        if (!IsEnabled(configuration) || identity is null || !string.Equals(GetMode(configuration), ModePrefer, StringComparison.Ordinal))
        {
            return true;
        }

        var preferredCount = 0;
        foreach (var issue in issues)
        {
            var eligibility = Evaluate(configuration, identity, issue);
            if (eligibility.IsEligible && eligibility.SelectionRank == 0)
            {
                preferredCount++;
                if (preferredCount >= requiredPreferredCount)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsIssueDerivedDeveloperWork(WorkflowItemType type) =>
        type is WorkflowItemType.ChangeRequest or WorkflowItemType.ResumeInProgressIssue or WorkflowItemType.NewIssue
            or WorkflowItemType.RecoverCompletedIssue or WorkflowItemType.RecoverCurrentPullRequest or WorkflowItemType.LinkPullRequestsToIssues;

    private static bool IsAllowedMode(string mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode) ? string.Empty : mode.Trim();
        return string.Equals(normalized, ModeIgnore, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, ModePrefer, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, ModeRequire, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedUnassigned(string unassigned)
    {
        var normalized = string.IsNullOrWhiteSpace(unassigned) ? string.Empty : unassigned.Trim();
        return string.Equals(normalized, UnassignedAllow, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, UnassignedExclude, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMode(string mode)
    {
        var normalized = string.IsNullOrWhiteSpace(mode) ? string.Empty : mode.Trim();
        if (string.Equals(normalized, ModePrefer, StringComparison.OrdinalIgnoreCase)) return ModePrefer;
        if (string.Equals(normalized, ModeRequire, StringComparison.OrdinalIgnoreCase)) return ModeRequire;
        return ModeIgnore;
    }

    private static string NormalizeUnassigned(string unassigned)
    {
        var normalized = string.IsNullOrWhiteSpace(unassigned) ? string.Empty : unassigned.Trim();
        if (string.Equals(normalized, UnassignedExclude, StringComparison.OrdinalIgnoreCase)) return UnassignedExclude;
        return UnassignedAllow;
    }

    private static AssignmentEligibility Eligible(
        AssignmentIdentity? identity,
        Issue issue,
        int selectionRank,
        bool assignedToCurrent,
        bool isUnassigned) => new()
        {
            IsEnabled = true,
            IsEligible = true,
            SelectionRank = selectionRank,
            AssignedToCurrentIdentity = assignedToCurrent,
            IsUnassigned = isUnassigned,
            IssueAssignees = GetAssignees(issue),
            CurrentIdentity = identity?.Name,
            Message = $"Issue #{issue.Number} is eligible under the assignment policy."
        };

    private static AssignmentEligibility Ineligible(
        AssignmentIdentity? identity,
        Issue issue,
        string message) => new()
        {
            IsEnabled = true,
            IsEligible = false,
            SelectionRank = int.MaxValue,
            AssignedToCurrentIdentity = false,
            IsUnassigned = GetAssignees(issue).Count == 0,
            IssueAssignees = GetAssignees(issue),
            CurrentIdentity = identity?.Name,
            Message = message
        };
}

public sealed class AssignmentIdentity
{
    public string? Name { get; init; }
    public IReadOnlyList<string> GitHubUsernames { get; init; } = Array.Empty<string>();
}

public sealed class AssignmentIdentityResolution
{
    public static AssignmentIdentityResolution NotEnabled { get; } = new() { IsEnabled = false, IsResolved = false };

    public static AssignmentIdentityResolution Failure(string message) => new() { IsEnabled = true, IsResolved = false, Message = message };

    public bool IsEnabled { get; init; }
    public bool IsResolved { get; init; }
    public AssignmentIdentity? Identity { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class AssignmentEligibility
{
    public static AssignmentEligibility Disabled { get; } = new() { IsEligible = true };

    public bool IsEnabled { get; init; }
    public bool IsEligible { get; init; }
    public int SelectionRank { get; init; }
    public bool AssignedToCurrentIdentity { get; init; }
    public bool IsUnassigned { get; init; }
    public IReadOnlyList<string> IssueAssignees { get; init; } = Array.Empty<string>();
    public string? CurrentIdentity { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record AssignmentIssueFilterResult(
    IReadOnlyList<Issue> EligibleIssues,
    IReadOnlyList<AssignmentEligibility> IneligibleIssues,
    bool NoEligibleWork,
    string Message);