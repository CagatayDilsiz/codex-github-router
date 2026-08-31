using System.Globalization;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;

namespace CodexGithubRouter.GitHub;

public static class GitHubCliService
{
    private const string ManagedLabelColor = "0E8A16";
    private const string ManagedLabelDescription = "Managed by Codex GitHub Router";

    public static async Task<HashSet<string>> GetRepositoryLabelNamesAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var process = await ProcessRunner.RunAsync(
            workingDirectory,
            "gh",
            new[] { "label", "list", "--limit", "1000", "--json", "name" },
            cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }

        try
        {
            var labels = System.Text.Json.JsonSerializer.Deserialize<List<GithubLabel>>(process.Output) ?? new List<GithubLabel>();
            return labels
                .Select(label => label.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidOperationException("Failed to deserialize GitHub CLI label output.", exception);
        }
    }

    public static async Task CreateLabelAsync(string workingDirectory, string labelName, CancellationToken cancellationToken = default)
    {
        var process = await ProcessRunner.RunAsync(
            workingDirectory,
            "gh",
            new[] { "label", "create", labelName, "--color", ManagedLabelColor, "--description", ManagedLabelDescription },
            cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }
    }

    public static async Task<PullRequest> GetPullRequestByNumberAsync(string workingDirectory, int pullRequestNumber, PullRequestSelection selection, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "pr",
            "view",
            pullRequestNumber.ToString(CultureInfo.InvariantCulture),
            "--json"          
        };

        arguments.Add(selection.ToSelectionString());

        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            if (IsConfirmedNotFound(process.Error))
            {
                throw new GitHubItemNotFoundException($"GitHub pull request was not found: {process.Error}");
            }
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }

        try
        {
            var pullRequest = System.Text.Json.JsonSerializer.Deserialize<PullRequest>(process.Output);
            if (pullRequest == null)
            {
                throw new InvalidOperationException("Failed to deserialize GitHub CLI output: output is null");
            }
            return pullRequest;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize GitHub CLI output", ex);
        }
    }

    public static async Task<List<PullRequest>> GetPullRequestsAsync(string workingDirectory, PullRequestFilters filters, PullRequestSelection selection, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "pr",
            "list"           
        };       

        if (filters is not null && !string.IsNullOrWhiteSpace(filters.State))
        {
            arguments.Add("--state");
            arguments.Add(filters.State);
        }

        arguments.Add("--json");
        arguments.Add(selection.ToSelectionString());

        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }

        try
        {
            var pullRequests = System.Text.Json.JsonSerializer.Deserialize<List<PullRequest>>(process.Output) ?? new List<PullRequest>();
            return pullRequests;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize GitHub CLI output", ex);
        }
    }

    public static async Task<List<Issue>> GetIssuesAsync(string workingDirectory, IssueFilters filters, bool addLinkedPRToSelection = false, CancellationToken cancellationToken = default)
    {      

        var arguments = new List<string>();
        arguments.Add("issue");     
        arguments.Add("list");
        arguments.Add("--state");
        arguments.Add("open");
        
        var searchQuery = BuildSearchQuery(filters);

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            arguments.Add("--search");
            arguments.Add(searchQuery);
        }

        if (filters.Limit is > 0)
        {
            arguments.Add("--limit");
            arguments.Add(filters.Limit.Value.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add("--json");

        if (addLinkedPRToSelection)
        {
            arguments.Add("number,title,url,labels,createdAt,updatedAt,closedByPullRequestsReferences,assignees");
        }
        else
        {
            arguments.Add("number,title,url,labels,createdAt,updatedAt,assignees");
        }
        
        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }

        try
        {
            var issues = System.Text.Json.JsonSerializer.Deserialize<List<Issue>>(process.Output) ?? new List<Issue>();
            return issues;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize GitHub CLI output",ex);    
        }        
    }

    public static async Task<List<Issue>> GetAllOpenIssuesByLabelsAsync(string workingDirectory, IReadOnlyCollection<string> labels, CancellationToken cancellationToken = default)
    {
        if (labels.Count == 0)
        {
            return new List<Issue>();
        }

        var repository = await ProcessRunner.RunAsync(workingDirectory, "gh", new[] { "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner" }, cancellationToken);
        if (repository.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {repository.ExitCode}: {repository.Error}");
        }

        var issueNumbers = new HashSet<int>();
        foreach (var label in labels.Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var process = await ProcessRunner.RunAsync(
                workingDirectory,
                "gh",
                new[]
                {
                    "api", "--paginate", "-X", "GET", $"repos/{repository.Output.Trim()}/issues",
                    "-f", "state=open", "-f", $"labels={label}", "-f", "per_page=100",
                    "--jq", ".[] | select(.pull_request == null) | .number"
                },
                cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
            }

            foreach (var line in process.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var issueNumber))
                {
                    issueNumbers.Add(issueNumber);
                }
            }
        }

        var issues = new List<Issue>();
        foreach (var issueNumber in issueNumbers.OrderBy(number => number))
        {
            issues.Add(await GetIssueByNumberAsync(workingDirectory, issueNumber, cancellationToken));
        }

        return issues;
    }

    public static async Task<Issue> GetIssueByNumberAsync(string workingDirectory, int issueNumber, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "issue",
            "view",
            issueNumber.ToString(CultureInfo.InvariantCulture),
            "--json",
            "number,title,url,labels,createdAt,updatedAt,state,closedByPullRequestsReferences,assignees"
        };

        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            if (IsConfirmedNotFound(process.Error))
            {
                throw new GitHubItemNotFoundException($"GitHub issue was not found: {process.Error}");
            }
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }

        try
        {
            var issue = System.Text.Json.JsonSerializer.Deserialize<Issue>(process.Output);
            if (issue == null)
            {
                throw new InvalidOperationException("Failed to deserialize GitHub CLI output: output is null");
            }
            return issue;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize GitHub CLI output", ex);
        }
    }

    public static async Task<bool> TransitionIssueAsync(string workingDirectory, IssueTransition issueTransition, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "issue",
            "edit",
            issueTransition.IssueNumber.ToString(CultureInfo.InvariantCulture)
        };

        if (issueTransition.LabelsToAdd.Any())
        {
             arguments.Add("--add-label");
             arguments.Add($"{string.Join(',', issueTransition.LabelsToAdd)}");
        }

        if (issueTransition.LabelsToRemove.Any())
        {
            arguments.Add("--remove-label");
            arguments.Add($"{string.Join(',', issueTransition.LabelsToRemove)}");
        }

        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }
        return true;
    }

    public static async Task CloseIssueAsync(string workingDirectory, int issueNumber, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "issue",
            "close",
            issueNumber.ToString(CultureInfo.InvariantCulture)
        };

        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }      
    }

    public static async Task<bool> TransitionPullRequestAsync(string workingDirectory, PullRequestTransition pullRequestTransition, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "pr",
            "edit",
            pullRequestTransition.PullRequestNumber.ToString(CultureInfo.InvariantCulture)
        };

        if (pullRequestTransition.LabelsToAdd.Any())
        {
             arguments.Add("--add-label");
             arguments.Add($"{string.Join(',', pullRequestTransition.LabelsToAdd)}");
        }

        if (pullRequestTransition.LabelsToRemove.Any())
        {
            arguments.Add("--remove-label");
            arguments.Add($"{string.Join(',', pullRequestTransition.LabelsToRemove)}");
        }

        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }
        return true;
    }

    public static async Task<string?> GetAuthenticatedUserAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", new[] { "api", "user", "--jq", ".login" }, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }

        var login = process.Output.Trim();
        return string.IsNullOrWhiteSpace(login) ? null : login;
    }

    public static string? BuildSearchQuery(IssueFilters filters)
    {
        var searchTerms = new List<string>();

        var labels = filters.Labels.Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (labels.Count > 0)
        {
            searchTerms.Add($"label:\"{string.Join("\",\"", labels.Select(label => label.Replace("\"", "\\\"", StringComparison.Ordinal)))}\"");
        }

        searchTerms.AddRange(filters.SearchTerms.Where(term => !string.IsNullOrWhiteSpace(term)));

        if (filters.SortBy.HasValue && filters.SortDirection.HasValue)
        {
            searchTerms.Add(BuildSortTerm(filters.SortBy.Value, filters.SortDirection.Value));
        }

        return searchTerms.Count == 0
            ? null
            : string.Join(' ', searchTerms);
    }

    private static string BuildSortTerm(IssueSortField field, SortDirection direction)
    {
        var fieldName = field switch
        {
            IssueSortField.CreatedAt => "created",
            IssueSortField.UpdatedAt => "updated",
            _ => throw new ArgumentOutOfRangeException(nameof(field),field, null)
        };

        var directionName = direction switch
        {
            SortDirection.Ascending => "asc",
            SortDirection.Descending => "desc",
            _ => throw new ArgumentOutOfRangeException(nameof(direction),direction,null)
        };

        return $"sort:{fieldName}-{directionName}";
    }

    public static bool IsConfirmedNotFound(string error) =>
        error.Contains("could not resolve to an issue", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("could not resolve to a pull request", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("could not resolve to a pullrequest", StringComparison.OrdinalIgnoreCase);
}
