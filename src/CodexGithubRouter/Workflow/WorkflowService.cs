using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

public static class WorkflowService
{
    public static async Task<WorkflowResponse> CheckNewIssuesAsync(RouterConfiguration configuration, string workingDirectory)
    {
         var customIssueSelection = new IssueSelectionConfiguration
        {
            Limit = 0 // No limit, we let gh cli handle the default which seems to be 30           
        };
        var openIssueFilters = IssueFilterResolver.ByState(configuration, WorkflowState.Ready, customIssueSelection);

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
                Tasks = new List<WorkflowTask>(),
                IsSuccessful = true,
                Message = "No new issues found."
            };
        }
    }

    public static async Task<WorkflowResponse> CheckCompletedIssuesAsync(RouterConfiguration configuration, string workingDirectory)
    {
        var customIssueSelection = new IssueSelectionConfiguration
        {
            Limit = 0 // No limit, we let gh cli handle the default which seems to be 30           
        };
        var completedIssueFilters = IssueFilterResolver.ByState(configuration, WorkflowState.Completed, customIssueSelection);

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
                Tasks = new List<WorkflowTask>(),
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

            var prList = new List<PullRequest>();

            foreach (var prReference in linkedPullRequests)
            {
                var prNumber = prReference.Number;
                var pr = await GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, prNumber, new PullRequestSelection() { Number=true, State=true, Labels = true }, CancellationToken.None);

                if (pr is not null)
                {
                    prList.Add(pr);
                }
            }

            if (prList.Count == 0)
            {
                // this should not happen? but if it does, what to do?               
                continue;
            }

            if (prList.Any(pr => pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase)))
            {
                // If any of the linked PRs is merged, we can close the issue automatically.
                await GitHubCliService.CloseIssueAsync(workingDirectory, issue.Number, CancellationToken.None);
                continue;
            }

            if (prList.All(pr => pr.State.Equals("closed", StringComparison.OrdinalIgnoreCase)))
            {
                // If all of the linked PRs are closed but not merged, we cannot close the issue automatically.

                workflowTasks.Add(new WorkflowTask()
                {
                    Type = TaskType.None,
                    IssueNumber = issue.Number,
                    PullRequestNumber = prList.First().Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = "All linked pull requests are closed but not merged. Please review the pull requests and ensure they are either merged or reopened or mark the issue to be worked on again.",
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),
                        HookBlocker = true
                    }
                    
                });                
            }

            if (prList.Any(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase)))
            {

                var openPullRequests = prList.Where(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase)).ToList();

                var changesRequested = openPullRequests.Where(pr => HasPullRequestState(pr, configuration, PullRequestState.ChangesRequested)).ToList();

                if (changesRequested.Count > 0)
                {
                    foreach (var pr in changesRequested)
                    {
                        workflowTasks.Add(new WorkflowTask()
                        {
                            Type = TaskType.ChangeRequest,
                            IssueNumber = issue.Number,
                            PullRequestNumber = pr.Number,
                            Status = new WorkflowTaskStatus
                            {
                                Message = $"Linked pull request #{pr.Number} for issue #{issue.Number} has requested changes. Please review the pull request and address the requested changes.",
                                LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),
                                HookBlocker = false
                            }
                        });
                    }

                    continue;
                }

                var reviewRequested = openPullRequests.FirstOrDefault(pr => HasPullRequestState(pr, configuration, PullRequestState.ReviewRequested));

                if (reviewRequested is not null)
                {

                    workflowTasks.Add(new WorkflowTask()
                    {
                        Type = TaskType.None,
                        IssueNumber = issue.Number,
                        PullRequestNumber = reviewRequested.Number,
                        Status = new WorkflowTaskStatus
                        {
                            Message = $"Linked pull request #{reviewRequested.Number} for issue #{issue.Number} is still under review. Please wait until the review is completed.",
                            LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),
                            HookBlocker = true
                        }
                    });

                    continue;
                }

                var awaitingMerge = openPullRequests.FirstOrDefault(pr => HasPullRequestState(pr, configuration, PullRequestState.AwaitingMerge));

                if (awaitingMerge is not null)
                {
                    workflowTasks.Add(new WorkflowTask()
                    {
                        Type = TaskType.None,
                        IssueNumber = issue.Number,
                        PullRequestNumber = awaitingMerge.Number,
                        Status = new WorkflowTaskStatus
                        {
                            Message = $"Linked pull request #{awaitingMerge.Number} for issue #{issue.Number} is awaiting merge. Please wait until the pull request is merged.",
                            LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),
                            HookBlocker = true
                        }
                    });

                    continue;
                }

                var allDeferred = openPullRequests.All(pr => HasPullRequestState(pr, configuration, PullRequestState.Deferred));

                if (allDeferred)
                {
                    // if the remaining open PRs are deferred, we can move to the next issue in the workflow. 
                    continue;
                }


                workflowTasks.Add(new WorkflowTask()
                {
                    Type = TaskType.None,
                    IssueNumber = issue.Number,
                    PullRequestNumber = openPullRequests.First().Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = $"Linked pull requests for issue #{issue.Number} are in an unknown state. Please review the pull requests and ensure they are in a valid state.",
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),
                        HookBlocker = true
                    }
                });               
            }
            else 
            {

                var prStates = prList.Select(pr => pr.State).Distinct().ToList();

                var unknownStates = prStates.Where(state => !state.Equals("merged", StringComparison.OrdinalIgnoreCase) && !state.Equals("closed", StringComparison.OrdinalIgnoreCase) && !state.Equals("open", StringComparison.OrdinalIgnoreCase)).ToList();
                // we checked all linked PRs and none of them is merged, closed, or open. This means they are in an unknown state.

                workflowTasks.Add(new WorkflowTask()
                {
                    Type = TaskType.None,
                    IssueNumber = issue.Number,
                    PullRequestNumber = prList.First().Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = $"Linked pull requests for issue #{issue.Number} are in unknown states: {string.Join(", ", unknownStates)}. Please review the pull requests and ensure they are in a valid state.",
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),
                        HookBlocker = true
                    }
                });                
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

    private static bool HasPullRequestState(PullRequest pullRequest, RouterConfiguration configuration, PullRequestState targetState)
    {
        if (!configuration.PullRequestStates.TryGetValue(targetState, out var stateRules) || stateRules.Count == 0)
        {
            return false;
        }

        var targetLabels = stateRules.Where(rule => rule.Type == IssueMatchRuleType.Label).SelectMany(state => state.Values).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (targetLabels.Count == 0)
        {
            return false;
        }

        var currentLabels = pullRequest.Labels.Select(label => label.Name).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return targetLabels.Any(label => currentLabels.Contains(label, StringComparer.OrdinalIgnoreCase));
    }
}