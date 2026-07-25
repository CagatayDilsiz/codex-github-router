using System.Globalization;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;

namespace CodexGithubRouter.GitHub;

public static class GitHubCliService
{

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
        
         foreach (var label in filters.Labels)
        {
            arguments.Add("--label");
            arguments.Add(label);
        }

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
            arguments.Add("number,title,url,labels,createdAt,updatedAt,closedByPullRequestsReferences");
        }
        else
        {
            arguments.Add("number,title,url,labels,createdAt,updatedAt");
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

    public static async Task<Issue> GetIssueByNumberAsync(string workingDirectory, int issueNumber, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "issue",
            "view",
            issueNumber.ToString(CultureInfo.InvariantCulture),
            "--json",
            "number,title,url,labels,createdAt,updatedAt"
        };

        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
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

    
    private static string? BuildSearchQuery(IssueFilters filters)
    {
        var searchTerms = new List<string>();

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
}