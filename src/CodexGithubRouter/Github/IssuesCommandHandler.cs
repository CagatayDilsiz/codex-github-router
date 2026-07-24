using CodexGithubRouter.Git;
namespace CodexGithubRouter.GitHub;

public static class IssuesCommandHandler
{
    public static async Task<int> HandleAsync(string[] args)
    {       

        var workingDirectory = args.Length > 0 ? args[0] : Environment.CurrentDirectory;

        try
        {
            var gitCommonDir = await GitRepositoryService.GetCommonDirectoryAsync(workingDirectory);

            if (gitCommonDir is null)
            {
                Console.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var issues = await GitHubCliService.GetOpenIssuesAsync(workingDirectory);

            if (issues.Count == 0)
            {
                Console.WriteLine("No open issues found.");
                return 0;
            }

            Console.WriteLine("Open Issues:");
            foreach (var issue in issues)
            {
                Console.WriteLine($"#{issue.Number}: {issue.Title} ({issue.Url})");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        
        return 0;
    }
}