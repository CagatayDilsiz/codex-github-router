using CodexGithubRouter.Helpers;

namespace CodexGithubRouter.Github;

public static class GithubCliService
{
    public static async Task<List<Issue>> GetOpenIssuesAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var issues = new List<Issue>();

        var arguments = new List<string>();
        arguments.Add("issue");     
        arguments.Add("list");
        arguments.Add("--state");
        arguments.Add("open");
        arguments.Add("--json");
        arguments.Add("number,title,url");    
        var process = await ProcessRunner.RunAsync(workingDirectory, "gh", arguments, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"GitHub CLI command failed with exit code {process.ExitCode}: {process.Error}");
        }

        issues = System.Text.Json.JsonSerializer.Deserialize<List<Issue>>(process.Output) ?? new List<Issue>();

        return issues;
    }
}