using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public static class WorkflowService
{
    public static async Task<WorkflowResponse> CheckNewIssuesAsync(RouterConfiguration configuration, string workingDirectory)
    {
        var openIssueFilters = IssueFilterResolver.ByState(configuration, WorkflowState.Ready);

        if (openIssueFilters is null)
        {
            return new WorkflowResponse
            {
                IsSuccessful = false,
                Message = "Could not resolve issue filters from workflow configuration."
            };
        }

        var openIssues = await GitHubCliService.GetIssuesAsync(workingDirectory, openIssueFilters, false);

        if (openIssues.Count > 0)
        {
            var workflowTasks = openIssues.Select(issue => new WorkflowTask
            {
                Type = TaskType.NewIssue,
                IssueNumber = issue.Number
            }).ToList();

            return new WorkflowResponse
            {
                Tasks = workflowTasks,
                IsSuccessful = true,
                Message = "New issues found."
            };
        }
        else
        {
            return new WorkflowResponse
            {
                IsSuccessful = false,
                Message = "No new issues found."
            };
        }
    }

    public static async Task<WorkflowResponse> CheckCompletedIssuesAsync(RouterConfiguration configuration, string workingDirectory)
    {
        var completedIssueFilters = IssueFilterResolver.ByState(configuration, WorkflowState.Completed);

        if (completedIssueFilters is null)
        {
            return new WorkflowResponse
            {
                IsSuccessful = false,
                Message = "Could not resolve issue filters from workflow configuration."
            };
        }

        var completedIssues = await GitHubCliService.GetIssuesAsync(workingDirectory, completedIssueFilters, true);

        if (completedIssues.Count > 0)
        {
            return await CheckIssueLinkedPullRequestsAsync(configuration, workingDirectory, completedIssues);
        }
        else
        {
            return new WorkflowResponse
            {
                IsSuccessful = true,
                Message = "No completed issues found."
            };
        }
    }

    public static async Task<WorkflowResponse> CheckIssueLinkedPullRequestsAsync(RouterConfiguration configuration, string workingDirectory, IEnumerable<Issue> completedIssues)
    {
        var workflowTasks = new List<WorkflowTask>();
        var noIssuesWithLinkedPRs = completedIssues.Where(issue => issue.ClosingPullRequestsReferences.Count == 0).ToList();
        var issuesWithLinkedPRs = completedIssues.Where(issue => issue.ClosingPullRequestsReferences.Count > 0).ToList();
        foreach (var issue in issuesWithLinkedPRs)
        {
            var linkedPullRequests = issue.ClosingPullRequestsReferences;

            foreach (var prReference in linkedPullRequests)
            {
                var prNumber = prReference.Number;
                var pr = await GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, prNumber, PullRequestSelection.SelectionWithAllFields(), CancellationToken.None);

                if (pr is null)
                {
                    continue;
                }

                if (pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase))
                {
                    // so despite linked PR is merged, issue is not closed automatically, so we can close the issue manually
                    await GitHubCliService.CloseIssueAsync(workingDirectory, issue.Number, CancellationToken.None);
                }
                else if (pr.State.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    var prLabels = pr.Labels.Select(label => label.Name).ToList();

                    var stateLabels = configuration.PullRequestStates.Where(x => x.Key == PullRequestState.changesRequested).SelectMany(x => x.Value).Where(y => y.Type == IssueMatchRuleType.Label).SelectMany(a => a.Values).ToList();

                    if (prLabels.Any(label => stateLabels.Contains(label)))
                    {
                        workflowTasks.Add(new WorkflowTask()
                        {
                            Type = TaskType.ChangeRequest,
                            IssueNumber = issue.Number,
                            PullRequestNumber = pr.Number,
                        });
                        continue;
                    }

                    // otherwise, linked PR is open and has no changes requested, so we can leave the issue open

                }
                else if (pr.State.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    // linked PR is closed but not merged, so we can leave the issue open
                    continue;
                }

            }
        }

        if (noIssuesWithLinkedPRs.Count > 0)
        {
            foreach (var issue in noIssuesWithLinkedPRs)
            {
                workflowTasks.Add(new WorkflowTask()
                {
                    Type = TaskType.ReviewPRForOpenIssues,
                    IssueNumber = issue.Number
                });
            }
        }

        return new WorkflowResponse
        {
            Tasks = workflowTasks,
            IsSuccessful = true,
            Message = "Workflow check completed successfully."
        };
    }
}