using CodexGithubRouter.Configurations;
using CodexGithubRouter.Git;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Explain;

public static class ExplainCommandHandler
{
    public static Task<int> HandleAsync(string[] args) => HandleAsync(args, new ExplainCommandDependencies(), CancellationToken.None);

    public static async Task<int> HandleAsync(string[] args, ExplainCommandDependencies dependencies, CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(args, out var issueNumber, out var pullRequestNumber, out var workingDirectory, out var model, out var usageError))
        {
            dependencies.Error.WriteLine(usageError);
            return 2;
        }

        try
        {
            var commonDirectory = await dependencies.GetGitCommonDirectoryAsync(workingDirectory, cancellationToken);
            if (commonDirectory is null)
            {
                dependencies.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var worktreeId = await dependencies.GetWorktreeIdAsync(workingDirectory, cancellationToken);
            if (worktreeId is null)
            {
                dependencies.Error.WriteLine("Not a valid Git repository.");
                return 1;
            }

            var configuration = await dependencies.LoadEffectiveConfigurationAsync(workingDirectory, cancellationToken);
            var activeClaim = await dependencies.ReadWorkClaimAsync(commonDirectory, worktreeId, cancellationToken);
            // Read-only diagnostics must apply the same stale-worktree evaluation production
            // pruning uses: a deleted worktree's claim is excluded (so it cannot occupy work in
            // the explanation) without writing to the claim file.
            var otherWorktreeClaims = (await dependencies.ReadWorkClaimsAsync(commonDirectory, cancellationToken))
                .Where(claim => !WorkClaimStore.IsStaleWorktree(commonDirectory, claim))
                .Where(claim => !string.Equals(
                    WorkClaimStore.NormalizeWorktreeId(commonDirectory, claim.WorktreeId),
                    WorkClaimStore.NormalizeWorktreeId(commonDirectory, worktreeId),
                    StringComparison.Ordinal))
                .ToList();

            var plan = await RoutingEvaluationService.EvaluateAsync(
                configuration,
                workingDirectory,
                currentModel: model,
                activeClaim: activeClaim,
                otherWorktreeClaims: otherWorktreeClaims,
                dependencies: dependencies.RoutingEvaluation.WithIdentityResolver(
                    (_, _) => ResolveIdentityAsync(configuration, workingDirectory, dependencies, cancellationToken)));

            if (!plan.IsSuccessful)
            {
                dependencies.Error.WriteLine($"Routing evaluation failed: {plan.DiscoveryFailureMessage}");
                return 1;
            }

            if (issueNumber.HasValue)
            {
                return await ExplainSingleIssueAsync(issueNumber.Value, plan, workingDirectory, dependencies, cancellationToken);
            }

            if (pullRequestNumber.HasValue)
            {
                return await ExplainSinglePullRequestAsync(pullRequestNumber.Value, plan, workingDirectory, dependencies, cancellationToken);
            }

            return await ExplainAllAsync(plan, workingDirectory, dependencies, cancellationToken);
        }
        catch (Exception exception)
        {
            dependencies.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ExplainSingleIssueAsync(
        int issueNumber,
        RoutingEvaluationResult plan,
        string workingDirectory,
        ExplainCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var issue = plan.ConsideredIssues.FirstOrDefault(candidate => candidate.Number == issueNumber);
        if (issue is null)
        {
            issue = await dependencies.GetIssueByNumberAsync(workingDirectory, issueNumber, cancellationToken);
        }

        if (issue is null)
        {
            dependencies.Error.WriteLine($"Issue #{issueNumber} not found.");
            return 1;
        }

        var explanation = RoutingExplanationService.Explain(plan, issue);
        dependencies.Output.WriteLine(RoutingExplanationService.FormatSingleExplanation(explanation));
        return 0;
    }

    private static async Task<int> ExplainSinglePullRequestAsync(
        int pullRequestNumber,
        RoutingEvaluationResult plan,
        string workingDirectory,
        ExplainCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        PullRequest pullRequest;
        try
        {
            pullRequest = await GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, ReviewRoutingService.Selection, cancellationToken);
        }
        catch (GitHubItemNotFoundException)
        {
            dependencies.Error.WriteLine($"Pull request #{pullRequestNumber} not found.");
            return 1;
        }

        var reviewerLogin = await ResolveAuthenticatedReviewerLoginAsync(workingDirectory, dependencies, cancellationToken);
        if (string.IsNullOrWhiteSpace(reviewerLogin))
        {
            dependencies.Error.WriteLine("Could not resolve the authenticated GitHub account to explain review routing. Run `gh auth status` and retry.");
            return 1;
        }

        var explanation = RoutingExplanationService.ExplainReview(plan, pullRequest, reviewerLogin, plan.AssignmentIdentity);
        dependencies.Output.WriteLine(RoutingExplanationService.FormatReviewExplanation(explanation));
        return 0;
    }

    private static async Task<int> ExplainAllAsync(
        RoutingEvaluationResult plan,
        string workingDirectory,
        ExplainCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var explanations = RoutingExplanationService.ExplainAll(plan);
        if (explanations.Count > 0)
        {
            lines.Add(RoutingExplanationService.FormatExplanations(explanations));
        }

        if (plan.ConsideredPullRequests.Count > 0)
        {
            var reviewerLogin = await ResolveAuthenticatedReviewerLoginAsync(workingDirectory, dependencies, cancellationToken);
            if (!string.IsNullOrWhiteSpace(reviewerLogin))
            {
                var reviewExplanations = RoutingExplanationService.ExplainReviewAll(plan, reviewerLogin, plan.AssignmentIdentity);
                if (reviewExplanations.Count > 0)
                {
                    if (lines.Count > 0)
                    {
                        lines.Add(string.Empty);
                    }

                    lines.Add(RoutingExplanationService.FormatReviewExplanations(reviewExplanations));
                }
            }
        }

        if (lines.Count == 0)
        {
            dependencies.Output.WriteLine("No issues found matching the workflow configuration.");
            return 0;
        }

        dependencies.Output.WriteLine(string.Join(Environment.NewLine, lines));
        return 0;
    }

    private static async Task<string?> ResolveAuthenticatedReviewerLoginAsync(
        string workingDirectory,
        ExplainCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        try
        {
            return await dependencies.ResolveAuthenticatedGitHubLoginAsync(workingDirectory, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<AssignmentIdentityResolution> ResolveIdentityAsync(
        RouterConfiguration configuration,
        string workingDirectory,
        ExplainCommandDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (!AssignmentRoutingService.RequiresLocalIdentity(configuration))
        {
            return AssignmentIdentityResolution.NotEnabled;
        }

        var gitIdentityValue = await dependencies.ResolveLocalIdentityAsync(workingDirectory, cancellationToken);
        var usernames = AssignmentRoutingService.ParseIdentityUsernames(gitIdentityValue);
        if (usernames.Count == 0)
        {
            string? authenticatedLogin = null;
            try
            {
                authenticatedLogin = await dependencies.ResolveAuthenticatedGitHubLoginAsync(workingDirectory, cancellationToken);
            }
            catch
            {
                // A missing or failing GitHub CLI must not crash the diagnostic; identity
                // resolution fails closed below when no CGR Git identity is configured.
            }

            if (!string.IsNullOrWhiteSpace(authenticatedLogin))
            {
                usernames = new[] { authenticatedLogin.Trim() };
            }
        }

        return AssignmentRoutingService.Resolve(configuration, usernames);
    }

    private static bool TryParseArguments(string[] args, out int? issueNumber, out int? pullRequestNumber, out string workingDirectory, out string? model, out string error)
    {
        issueNumber = null;
        pullRequestNumber = null;
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
                    error = "cgr explain: --model requires a value.";
                    return false;
                }

                model = args[++index];
                continue;
            }

            if (string.Equals(argument, "--issue", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var number))
                {
                    error = "cgr explain: --issue requires a numeric issue number.";
                    return false;
                }

                issueNumber = number;
                index++;
                continue;
            }

            if (string.Equals(argument, "--pr", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var number))
                {
                    error = "cgr explain: --pr requires a numeric pull request number.";
                    return false;
                }

                pullRequestNumber = number;
                index++;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"cgr explain: unknown option: {argument}";
                return false;
            }

            positionals.Add(argument);
        }

        if (positionals.Count > 1)
        {
            error = "cgr explain: too many arguments.";
            return false;
        }

        if (positionals.Count == 1)
        {
            workingDirectory = positionals[0];
        }

        return true;
    }
}

public sealed class ExplainCommandDependencies
{
    public TextWriter Output { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    public Func<string, CancellationToken, Task<string?>> GetGitCommonDirectoryAsync { get; init; }
        = (workingDirectory, cancellationToken) => GitRepositoryService.GetCommonDirectoryAsync(workingDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> GetWorktreeIdAsync { get; init; }
        = (workingDirectory, cancellationToken) => GitRepositoryService.GetWorktreeIdAsync(workingDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<RouterConfiguration>> LoadEffectiveConfigurationAsync { get; init; }
        = (workingDirectory, cancellationToken) => WorkflowConfigurationService.LoadEffectiveAsync(workingDirectory, cancellationToken);

    public Func<string, int, CancellationToken, Task<Issue?>> GetIssueByNumberAsync { get; init; }
        = async (workingDirectory, issueNumber, cancellationToken) =>
        {
            try
            {
                return await GitHubCliService.GetIssueByNumberAsync(workingDirectory, issueNumber, cancellationToken);
            }
            catch (GitHubItemNotFoundException)
            {
                return null;
            }
        };

    public RoutingEvaluationDependencies RoutingEvaluation { get; init; } = new();

    public Func<string, string, CancellationToken, Task<WorkClaim?>> ReadWorkClaimAsync { get; init; }
        = (gitCommonDirectory, worktreeId, cancellationToken) => WorkClaimStore.TryReadAsync(gitCommonDirectory, worktreeId, cancellationToken);

    public Func<string, CancellationToken, Task<IReadOnlyList<WorkClaim>>> ReadWorkClaimsAsync { get; init; }
        = (gitCommonDirectory, cancellationToken) => WorkClaimStore.TryReadActiveClaimsAsync(gitCommonDirectory, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> ResolveLocalIdentityAsync { get; init; }
        = (repositoryRoot, cancellationToken) => GitRepositoryService.GetConfigValueAsync(repositoryRoot, AssignmentRoutingService.LocalIdentityConfigKey, cancellationToken);

    public Func<string, CancellationToken, Task<string?>> ResolveAuthenticatedGitHubLoginAsync { get; init; }
        = (repositoryRoot, cancellationToken) => GitHubCliService.GetAuthenticatedUserAsync(repositoryRoot, cancellationToken);
}