using System.Globalization;
using CodexGithubRouter.Configurations;
using CodexGithubRouter.Helpers;

namespace CodexGithubRouter.GitHub;

public static class GitHubCliService
{
    public static async Task<List<Issue>> GetIssuesAsync(string workingDirectory, IssueFilters filters, CancellationToken cancellationToken = default)
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
        arguments.Add("number,title,url,labels,createdAt,updatedAt");  
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