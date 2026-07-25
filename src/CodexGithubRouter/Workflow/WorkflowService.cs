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
                var pr = await GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, prNumber, new PullRequestSelection() { Number=true, State=true, Labels = true }, CancellationToken.None);

                if (pr is null)
                {
                    continue;
                }

                if (pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase))
                {
                    // so despite linked PR is merged, issue is not closed automatically, so we can close the issue manually
                    await GitHubCliService.CloseIssueAsync(workingDirectory, issue.Number, CancellationToken.None);

                    break;
                }
                else if (pr.State.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    var prLabels = pr.Labels.Select(label => label.Name).ToList();

                    var reviewRequestedLabels = configuration.PullRequestStates.Where(x => x.Key == PullRequestState.ReviewRequested).SelectMany(x => x.Value).Where(y => y.Type == IssueMatchRuleType.Label).SelectMany(a => a.Values).ToList();

                    if (prLabels.Any(reviewRequestedLabels.Contains))
                    {
                        return new WorkflowResponse
                        {
                            IsSuccessful = false,
                            Message = $"Linked pull request #{pr.Number} for issue #{issue.Number} is still under review. Please wait until the review is completed."
                        };                       
                    }
                    
                    var stateLabels = configuration.PullRequestStates.Where(x => x.Key == PullRequestState.ChangeRequested).SelectMany(x => x.Value).Where(y => y.Type == IssueMatchRuleType.Label).SelectMany(a => a.Values).ToList();

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
                    else
                    {
                        var awaitingMergeLabels = configuration.PullRequestStates.Where(x => x.Key == PullRequestState.AwaitingMerge).SelectMany(x => x.Value).Where(y => y.Type == IssueMatchRuleType.Label).SelectMany(a => a.Values).ToList();

                        if (prLabels.Any(label => awaitingMergeLabels.Contains(label)))
                        {
                            // linked PR is open and has awaiting merge label, so we can leave the issue open and move to the next issue
                            continue;
                        }
                        else
                        {
                            return new WorkflowResponse
                            {
                                IsSuccessful = false,
                                Message = $"Linked pull request #{pr.Number} for issue #{issue.Number} is open but does not have any of the expected labels for 'changes requested' or 'awaiting merge'. Please review the pull request and ensure it has the correct labels."
                            };
                        }
                    }                   

                }
                else if (pr.State.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    return new WorkflowResponse
                    {
                        IsSuccessful = false,
                        Message = $"Linked pull request #{pr.Number} for issue #{issue.Number} is closed but not merged. Please review the pull request and ensure it is either merged or reopened or mark the issue to be worked on again."
                    };
                    
                }

            }
        }

        if (noIssuesWithLinkedPRs.Count > 0)
        {
            foreach (var issue in noIssuesWithLinkedPRs)
            {
                workflowTasks.Add(new WorkflowTask()
                {
                    Type = TaskType.LinkPullRequestsToIssues,
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