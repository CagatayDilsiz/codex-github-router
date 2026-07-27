using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Workflow;

public static class WorkflowService
{
    public static async Task<WorkflowResponse> CheckClaimedWorkAsync(RouterConfiguration configuration, string workingDirectory, WorkClaim claim)
    {
        var issue = await GitHubCliService.GetIssueByNumberAsync(workingDirectory, claim.IssueNumber, CancellationToken.None);
        return await EvaluateClaimedWorkAsync(configuration, claim, issue, pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, new PullRequestSelection { Number = true, State = true, Labels = true }, CancellationToken.None));
    }

    public static async Task<WorkflowResponse> EvaluateClaimedWorkAsync(RouterConfiguration configuration, WorkClaim claim, Issue issue, Func<int, Task<PullRequest>> getPullRequest)
    {
        var issueResolution = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States);
        if (issueResolution.IsAmbiguous)
        {
            return new WorkflowResponse { IsSuccessful = false, Message = issueResolution.DescribeConflict($"claimed issue #{claim.IssueNumber}") };
        }

        if (!claim.PullRequestNumber.HasValue)
        {
            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.Ready))
            {
                return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.NewIssue, IssueNumber = claim.IssueNumber } } };
            }

            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.InProgress) && issue.ClosingPullRequestsReferences.Count == 0)
            {
                return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = claim.IssueNumber } } };
            }

            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.Completed) && issue.ClosingPullRequestsReferences.Count == 0)
            {
                return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.LinkPullRequestsToIssues, IssueNumber = claim.IssueNumber } } };
            }

            if (!issueResolution.MatchedLabels.ContainsKey(WorkflowState.InProgress) && !issueResolution.MatchedLabels.ContainsKey(WorkflowState.Completed))
            {
                return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} cannot be resolved from its current issue state." };
            }

            if (issue.ClosingPullRequestsReferences.Count != 1)
            {
                return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} has multiple linked pull requests and cannot choose a pull-request identity implicitly." };
            }

            var linkedPullRequest = await getPullRequest(issue.ClosingPullRequestsReferences[0].Number);
            return EvaluateClaimedPullRequest(configuration, claim, linkedPullRequest);
        }

        if (!issue.ClosingPullRequestsReferences.Any(reference => reference.Number == claim.PullRequestNumber.Value))
        {
            return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} / pull request #{claim.PullRequestNumber.Value} cannot be resolved because the pull request does not close the claimed issue." };
        }

        var pullRequest = await getPullRequest(claim.PullRequestNumber.Value);
        return EvaluateClaimedPullRequest(configuration, claim, pullRequest);
    }

    private static WorkflowResponse EvaluateClaimedPullRequest(RouterConfiguration configuration, WorkClaim claim, PullRequest pullRequest)
    {
        var pullRequestResolution = WorkflowStateResolver.Resolve(pullRequest.Labels.Select(label => label.Name), configuration.PullRequestStates);
        if (pullRequestResolution.IsAmbiguous)
        {
            return new WorkflowResponse { IsSuccessful = false, Message = pullRequestResolution.DescribeConflict($"claimed pull request #{pullRequest.Number}") };
        }

        if (!string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} / pull request #{pullRequest.Number} cannot be resolved because the pull request is {pullRequest.State}." };
        }

        if (pullRequestResolution.MatchedLabels.ContainsKey(PullRequestState.ChangesRequested))
        {
            return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.ChangeRequest, IssueNumber = claim.IssueNumber, PullRequestNumber = pullRequest.Number } } };
        }

        if (pullRequestResolution.MatchedLabels.ContainsKey(PullRequestState.ReviewRequested))
        {
            return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.AwaitingReview, IssueNumber = claim.IssueNumber, PullRequestNumber = pullRequest.Number } } };
        }

        if (pullRequestResolution.MatchedLabels.ContainsKey(PullRequestState.AwaitingMerge))
        {
            return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.AwaitingMerge, IssueNumber = claim.IssueNumber, PullRequestNumber = pullRequest.Number } } };
        }

        if (pullRequestResolution.MatchedLabels.ContainsKey(PullRequestState.Deferred))
        {
            return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.Deferred, IssueNumber = claim.IssueNumber, PullRequestNumber = pullRequest.Number } } };
        }

        return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} / pull request #{pullRequest.Number} has no actionable claimed pull-request state." };
    }

    public static async Task<WorkflowResponse> CheckInProgressIssuesAsync(RouterConfiguration configuration, string workingDirectory, int scanLimit = 30)
    {
        var inProgressFilters = IssueFilterResolver.ByState(configuration, WorkflowState.InProgress, scanLimit);
        var inProgressIssues = await GitHubCliService.GetIssuesAsync(workingDirectory, inProgressFilters, true);

        var conflict = FindIssueConflict(inProgressIssues, configuration);
        if (conflict is not null) return conflict;

        return await EvaluateInProgressIssuesAsync(
            configuration,
            inProgressIssues,
            pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, new PullRequestSelection { Number = true, State = true, Labels = true }, CancellationToken.None));
    }

    public static async Task<WorkflowResponse> EvaluateInProgressIssuesAsync(RouterConfiguration configuration, IReadOnlyList<Issue> inProgressIssues, Func<int, Task<PullRequest>> getPullRequest)
    {
        if (inProgressIssues.Count > 1)
        {
            return new WorkflowResponse
            {
                IsSuccessful = false,
                Message = $"Multiple issues are marked as in progress: {string.Join(", ", inProgressIssues.Select(issue => $"#{issue.Number}"))}. Resolve the workflow state before starting new work."
            };
        }

        if (inProgressIssues.Count == 0)
        {
            return new WorkflowResponse
            {
                IsSuccessful = true,
                Message = "No in-progress issues found."
            };
        }

        var issue = inProgressIssues[0];
        if (issue.ClosingPullRequestsReferences.Count > 0)
        {
            return await CheckIssueLinkedPullRequestsAsync(configuration, new[] { issue }, getPullRequest);
        }

        return new WorkflowResponse
        {
            IsSuccessful = true,
            Message = $"Issue #{issue.Number} is already in progress.",
            Tasks = new List<WorkflowItem>
            {
                new()
                {
                    Type = WorkflowItemType.ResumeInProgressIssue,
                    IssueNumber = issue.Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = $"Issue #{issue.Number} is already in progress and has no linked pull request. Resume or report the existing work; do not start a new issue."
                    }
                }
            }
        };
    }

    public static async Task<WorkflowResponse> CheckNewIssuesAsync(RouterConfiguration configuration, string workingDirectory, int scanLimit = 30)
    {        
        var openIssueFilters = IssueFilterResolver.ByState(configuration, WorkflowState.Ready, scanLimit);

        if (openIssueFilters is null)
        {
            return new WorkflowResponse
            {
                IsSuccessful = false,
                Message = "Could not resolve issue filters from workflow configuration."
            };
        }

        var openIssues = await GitHubCliService.GetIssuesAsync(workingDirectory, openIssueFilters, false);

        var conflict = FindIssueConflict(openIssues, configuration);
        if (conflict is not null) return conflict;

        if (openIssues.Count > 0)
        {
            var workflowTasks = openIssues.Select(issue => new WorkflowItem
            {
                Type = WorkflowItemType.NewIssue,
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
                Tasks = new List<WorkflowItem>(),
                IsSuccessful = true,
                Message = "No new issues found."
            };
        }
    }

    public static async Task<WorkflowResponse> CheckCompletedIssuesAsync(RouterConfiguration configuration, string workingDirectory, int scanLimit = 30)
    {
        
        var completedIssueFilters = IssueFilterResolver.ByState(configuration, WorkflowState.Completed, scanLimit);

        if (completedIssueFilters is null)
        {
            return new WorkflowResponse
            {
                IsSuccessful = false,
                Message = "Could not resolve issue filters from workflow configuration."
            };
        }       

        var completedIssues = await GitHubCliService.GetIssuesAsync(workingDirectory, completedIssueFilters, true);

        var conflict = FindIssueConflict(completedIssues, configuration);
        if (conflict is not null) return conflict;

        if (completedIssues.Count > 0)
        {
            return await CheckIssueLinkedPullRequestsAsync(configuration, workingDirectory, completedIssues);
        }
        else
        {
            return new WorkflowResponse
            {
                Tasks = new List<WorkflowItem>(),
                IsSuccessful = true,
                Message = "No completed issues found."
            };
        }
    }

    public static async Task<WorkflowResponse> CheckIssueLinkedPullRequestsAsync(RouterConfiguration configuration, string workingDirectory, IEnumerable<Issue> completedIssues)
    {
        return await CheckIssueLinkedPullRequestsAsync(
            configuration,
            completedIssues,
            pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, new PullRequestSelection { Number = true, State = true, Labels = true }, CancellationToken.None));
    }

    public static async Task<WorkflowResponse> CheckIssueLinkedPullRequestsAsync(RouterConfiguration configuration, IEnumerable<Issue> completedIssues, Func<int, Task<PullRequest>> getPullRequest)
    {
        var workflowTasks = new List<WorkflowItem>();
        var noIssuesWithLinkedPRs = completedIssues.Where(issue => issue.ClosingPullRequestsReferences.Count == 0).ToList();
        var issuesWithLinkedPRs = completedIssues.Where(issue => issue.ClosingPullRequestsReferences.Count > 0).ToList();        

        foreach (var issue in issuesWithLinkedPRs)
        {
            var linkedPullRequests = issue.ClosingPullRequestsReferences;

            var prList = new List<PullRequest>();

            foreach (var prReference in linkedPullRequests)
            {
                var prNumber = prReference.Number;
                var pr = await getPullRequest(prNumber);

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
                workflowTasks.Add(new WorkflowItem()
                {
                    Type = WorkflowItemType.CloseIssue,
                    IssueNumber = issue.Number,
                    PullRequestNumber = prList.First(pr => pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase)).Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = $"Linked pull request #{prList.First(pr => pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase)).Number} for issue #{issue.Number} is merged. The issue can be closed automatically.",
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),                        
                    }
                });
                
                continue;
            }

            var pullRequestConflict = prList.Where(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase)).Select(pr => new { PullRequest = pr, Resolution = WorkflowStateResolver.Resolve(pr.Labels.Select(label => label.Name), configuration.PullRequestStates) }).FirstOrDefault(entry => entry.Resolution.IsAmbiguous);
            if (pullRequestConflict is not null)
            {
                workflowTasks.Add(new WorkflowItem { Type = WorkflowItemType.UnknownPullRequestState, IssueNumber = issue.Number, PullRequestNumber = pullRequestConflict.PullRequest.Number, Status = new WorkflowTaskStatus { Message = pullRequestConflict.Resolution.DescribeConflict($"pull request #{pullRequestConflict.PullRequest.Number}"), LinkedPullRequests = prList.Select(pr => pr.Number).ToList() } });
                continue;
            }

            if (prList.All(pr => pr.State.Equals("closed", StringComparison.OrdinalIgnoreCase)))
            {
                // If all of the linked PRs are closed but not merged, we cannot close the issue automatically.

                workflowTasks.Add(new WorkflowItem()
                {
                    Type = WorkflowItemType.ClosedWithoutMerge,
                    IssueNumber = issue.Number,
                    PullRequestNumber = prList.First().Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = "All linked pull requests are closed but not merged. Please review the pull requests and ensure they are either merged or reopened or mark the issue to be worked on again.",
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),                        
                    }
                    
                });    

                continue;            
            }

            if (prList.Any(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase)))
            {

                var openPullRequests = prList.Where(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase)).ToList();

                var changesRequested = openPullRequests.Where(pr => HasPullRequestState(pr, configuration, PullRequestState.ChangesRequested)).ToList();

                if (changesRequested.Count > 0)
                {
                    foreach (var pr in changesRequested)
                    {
                        workflowTasks.Add(new WorkflowItem()
                        {
                            Type = WorkflowItemType.ChangeRequest,
                            IssueNumber = issue.Number,
                            PullRequestNumber = pr.Number,
                            Status = new WorkflowTaskStatus
                            {
                                Message = $"Linked pull request #{pr.Number} for issue #{issue.Number} has requested changes. Please review the pull request and address the requested changes.",
                                LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),                               
                            }
                        });
                    }

                    continue;
                }

                var reviewRequested = openPullRequests.FirstOrDefault(pr => HasPullRequestState(pr, configuration, PullRequestState.ReviewRequested));

                if (reviewRequested is not null)
                {

                    workflowTasks.Add(new WorkflowItem()
                    {
                        Type = WorkflowItemType.AwaitingReview,
                        IssueNumber = issue.Number,
                        PullRequestNumber = reviewRequested.Number,
                        Status = new WorkflowTaskStatus
                        {
                            Message = $"Linked pull request #{reviewRequested.Number} for issue #{issue.Number} is still under review. Please wait until the review is completed.",
                            LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),                           
                        }
                    });

                    continue;
                }

                var awaitingMerge = openPullRequests.FirstOrDefault(pr => HasPullRequestState(pr, configuration, PullRequestState.AwaitingMerge));

                if (awaitingMerge is not null)
                {
                    workflowTasks.Add(new WorkflowItem()
                    {
                        Type = WorkflowItemType.AwaitingMerge,
                        IssueNumber = issue.Number,
                        PullRequestNumber = awaitingMerge.Number,
                        Status = new WorkflowTaskStatus
                        {
                            Message = $"Linked pull request #{awaitingMerge.Number} for issue #{issue.Number} is awaiting merge. Please wait until the pull request is merged.",
                            LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),
                          
                        }
                    });

                    continue;
                }

                var allDeferred = openPullRequests.All(pr => HasPullRequestState(pr, configuration, PullRequestState.Deferred));

                if (allDeferred)
                {
                    workflowTasks.Add(new WorkflowItem()
                    {
                        Type = WorkflowItemType.Deferred,
                        IssueNumber = issue.Number,
                        PullRequestNumber = openPullRequests.First().Number,
                        Status = new WorkflowTaskStatus
                        {
                            Message = $"All linked pull requests for issue #{issue.Number} are deferred. No action is required at this time.",
                            LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),                           
                        }
                    });
                   
                    continue;
                }


                workflowTasks.Add(new WorkflowItem()
                {
                    Type = WorkflowItemType.UnknownPullRequestState,
                    IssueNumber = issue.Number,
                    PullRequestNumber = openPullRequests.First().Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = $"Linked pull requests for issue #{issue.Number} are in an unknown state. Please review the pull requests and ensure they are in a valid state.",
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),                 
                    }
                });               
            }
            else 
            {

                var prStates = prList.Select(pr => pr.State).Distinct().ToList();

                var unknownStates = prStates.Where(state => !state.Equals("merged", StringComparison.OrdinalIgnoreCase) && !state.Equals("closed", StringComparison.OrdinalIgnoreCase) && !state.Equals("open", StringComparison.OrdinalIgnoreCase)).ToList();
                // we checked all linked PRs and none of them is merged, closed, or open. This means they are in an unknown state.

                workflowTasks.Add(new WorkflowItem()
                {
                    Type = WorkflowItemType.UnknownPullRequestState,
                    IssueNumber = issue.Number,
                    PullRequestNumber = prList.First().Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = $"Linked pull requests for issue #{issue.Number} are in unknown states: {string.Join(", ", unknownStates)}. Please review the pull requests and ensure they are in a valid state.",
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList(),                   
                    }
                });                
            }            
        }

        if (noIssuesWithLinkedPRs.Count > 0)
        {
            foreach (var issue in noIssuesWithLinkedPRs)
            {
                workflowTasks.Add(new WorkflowItem()
                {
                    Type = WorkflowItemType.LinkPullRequestsToIssues,
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
        var resolution = WorkflowStateResolver.Resolve(pullRequest.Labels.Select(label => label.Name), configuration.PullRequestStates);
        if (resolution.IsAmbiguous)
        {
            return false;
        }

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

    private static WorkflowResponse? FindIssueConflict(IEnumerable<Issue> issues, RouterConfiguration configuration)
    {
        var conflict = issues.Select(issue => new { Issue = issue, Resolution = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States) }).FirstOrDefault(entry => entry.Resolution.IsAmbiguous);
        return conflict is null ? null : new WorkflowResponse { IsSuccessful = false, Message = conflict.Resolution.DescribeConflict($"issue #{conflict.Issue.Number}") };
    }
}
