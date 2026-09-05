using CodexGithubRouter.Configurations;
using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Workflow;

public static class WorkflowService
{
    public static async Task<WorkflowResponse> CheckClaimedWorkAsync(RouterConfiguration configuration, string workingDirectory, WorkClaim claim, string? currentModel = null)
    {
        var issue = await GitHubCliService.GetIssueByNumberAsync(workingDirectory, claim.IssueNumber, CancellationToken.None);
        var response = await EvaluateClaimedWorkAsync(configuration, claim, issue, pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(
            workingDirectory,
            pullRequestNumber,
            new PullRequestSelection
            {
                Number = true,
                State = true,
                Labels = true,
                CreatedAt = true,
                HeadRefName = true,
                ClosingIssuesReferences = true
            },
            CancellationToken.None), currentModel, issueNumber => GitHubCliService.GetIssueByNumberAsync(workingDirectory, issueNumber, CancellationToken.None));
        if (response.ConsideredIssues.Count == 0)
        {
            response.ConsideredIssues.Add(issue);
        }

        return response;
    }

    public static async Task<WorkflowResponse> EvaluateClaimedWorkAsync(RouterConfiguration configuration, WorkClaim claim, Issue issue, Func<int, Task<PullRequest>> getPullRequest, string? currentModel = null, Func<int, Task<Issue>>? getIssue = null)
    {
        var eligibility = WorkerRoutingService.EvaluateClaim(configuration, claim, issue, currentModel);
        if (eligibility.IsEnabled && !eligibility.IsEligible)
        {
            return new WorkflowResponse { IsSuccessful = false, Message = eligibility.Message };
        }

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

            if (!issueResolution.MatchedLabels.ContainsKey(WorkflowState.InProgress) && !issueResolution.MatchedLabels.ContainsKey(WorkflowState.Completed))
            {
                return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} cannot be resolved from its current issue state." };
            }

            if (issue.ClosingPullRequestsReferences.Count == 0)
            {
                return new WorkflowResponse
                {
                    IsSuccessful = true,
                    Tasks = new List<WorkflowItem>
                    {
                        new()
                        {
                            Type = issueResolution.MatchedLabels.ContainsKey(WorkflowState.InProgress)
                                ? WorkflowItemType.ResumeInProgressIssue
                                : WorkflowItemType.RecoverCompletedIssue,
                            IssueNumber = claim.IssueNumber
                        }
                    }
                };
            }

            var currentPullRequests = await GetCurrentClaimPullRequestsAsync(claim, issue, getPullRequest);
            if (currentPullRequests.Count > 1)
            {
                return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} has multiple current pull requests and cannot choose a pull-request identity implicitly." };
            }

            if (currentPullRequests.Count == 0)
            {
                if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.InProgress))
                {
                    return new WorkflowResponse { IsSuccessful = true, Tasks = new List<WorkflowItem> { new() { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = claim.IssueNumber } } };
                }

                return new WorkflowResponse
                {
                    IsSuccessful = true,
                    Tasks = new List<WorkflowItem>
                    {
                        new()
                        {
                            Type = WorkflowItemType.RecoverCompletedIssue,
                            IssueNumber = claim.IssueNumber,
                            Status = new WorkflowTaskStatus
                            {
                                Message = $"Completed issue #{claim.IssueNumber} needs safe branch and pull-request recovery."
                            }
                        }
                    }
                };
            }

            var currentPullRequest = currentPullRequests[0];
            var workerConflict = claim.WorkType == WorkClaimType.ChangeRequest || HasPullRequestState(currentPullRequest, configuration, PullRequestState.ChangesRequested)
                ? await FindPullRequestWorkerConflictAsync(configuration, new[] { currentPullRequest }, new[] { issue }, getIssue)
                : null;
            if (workerConflict is not null)
            {
                return new WorkflowResponse { IsSuccessful = false, Message = workerConflict.Message };
            }

            return EvaluateClaimedPullRequest(configuration, claim, currentPullRequest);
        }

        if (!issue.ClosingPullRequestsReferences.Any(reference => reference.Number == claim.PullRequestNumber.Value))
        {
            return new WorkflowResponse { IsSuccessful = false, Message = $"Active work claim for issue #{claim.IssueNumber} / pull request #{claim.PullRequestNumber.Value} cannot be resolved because the pull request does not close the claimed issue." };
        }

        var pullRequest = await getPullRequest(claim.PullRequestNumber.Value);
        var pullRequestWorkerConflict = claim.WorkType == WorkClaimType.ChangeRequest || HasPullRequestState(pullRequest, configuration, PullRequestState.ChangesRequested)
            ? await FindPullRequestWorkerConflictAsync(configuration, new[] { pullRequest }, new[] { issue }, getIssue)
            : null;
        if (pullRequestWorkerConflict is not null)
        {
            return new WorkflowResponse { IsSuccessful = false, Message = pullRequestWorkerConflict.Message };
        }

        return EvaluateClaimedPullRequest(configuration, claim, pullRequest);
    }

    private static async Task<List<PullRequest>> GetCurrentClaimPullRequestsAsync(
        WorkClaim claim,
        Issue issue,
        Func<int, Task<PullRequest>> getPullRequest)
    {
        var currentPullRequests = new List<PullRequest>();
        foreach (var reference in issue.ClosingPullRequestsReferences)
        {
            PullRequest pullRequest;
            try
            {
                pullRequest = await getPullRequest(reference.Number);
            }
            catch (GitHubItemNotFoundException)
            {
                continue;
            }

            if (IsCurrentClaimPullRequest(claim, issue, pullRequest))
            {
                currentPullRequests.Add(pullRequest);
            }
        }

        return currentPullRequests;
    }

    public static bool IsCurrentClaimPullRequest(WorkClaim claim, Issue issue, PullRequest pullRequest)
    {
        // A claim without a GitHub-derived baseline cannot prove ownership of
        // a linked PR. This is intentionally conservative for legacy claim
        // files written before ClaimedIssueUpdatedAt was persisted.
        if (claim.ClaimedIssueUpdatedAt == default || pullRequest.CreatedAt < claim.ClaimedIssueUpdatedAt)
        {
            return false;
        }

        if (!pullRequest.ClosingIssuesReferences.Any(reference => reference.Number == issue.Number))
        {
            return false;
        }

        var branchPrefix = $"codex/issue-{issue.Number}";
        return string.Equals(pullRequest.HeadRefName, branchPrefix, StringComparison.OrdinalIgnoreCase) ||
            pullRequest.HeadRefName.StartsWith(branchPrefix + "-", StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowResponse EvaluateClaimedPullRequest(RouterConfiguration configuration, WorkClaim claim, PullRequest pullRequest)
    {
        var pullRequestResolution = WorkflowStateResolver.Resolve(pullRequest.Labels.Select(label => label.Name), configuration.PullRequestStates);
        if (pullRequestResolution.IsAmbiguous)
        {
            return new WorkflowResponse { IsSuccessful = false, Message = pullRequestResolution.DescribeConflict($"claimed pull request #{pullRequest.Number}") };
        }

        if (string.Equals(pullRequest.State, "merged", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem>
                {
                    new()
                    {
                        Type = WorkflowItemType.CloseIssue,
                        IssueNumber = claim.IssueNumber,
                        PullRequestNumber = pullRequest.Number,
                        Status = new WorkflowTaskStatus { Message = $"Linked pull request #{pullRequest.Number} is merged and issue #{claim.IssueNumber} can be closed." }
                    }
                }
            };
        }

        if (string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowResponse
            {
                IsSuccessful = true,
                Tasks = new List<WorkflowItem>
                {
                    new()
                    {
                        Type = WorkflowItemType.ClosedWithoutMerge,
                        IssueNumber = claim.IssueNumber,
                        PullRequestNumber = pullRequest.Number,
                        Status = new WorkflowTaskStatus { Message = $"Linked pull request #{pullRequest.Number} is closed without merge. Review the pull request before continuing." }
                    }
                }
            };
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

        return new WorkflowResponse
        {
            IsSuccessful = true,
            Tasks = new List<WorkflowItem>
            {
                new()
                {
                    Type = WorkflowItemType.RecoverCurrentPullRequest,
                    IssueNumber = claim.IssueNumber,
                    PullRequestNumber = pullRequest.Number,
                    Status = new WorkflowTaskStatus
                    {
                        Message = $"Current pull request #{pullRequest.Number} for issue #{claim.IssueNumber} has no workflow label and needs lifecycle recovery."
                    }
                }
            }
        };
    }

    public static async Task<WorkflowResponse> CheckInProgressIssuesAsync(RouterConfiguration configuration, string workingDirectory, int scanLimit = 30, string? currentModel = null, AssignmentIdentity? assignmentIdentity = null)
    {
        var inProgressFilters = IssueFilterResolver.ByState(configuration, WorkflowState.InProgress, scanLimit);
        var discovery = await DiscoverIssuesAsync(configuration, workingDirectory, inProgressFilters, true, scanLimit, currentModel, assignmentIdentity);
        var inProgressIssues = discovery.Issues;

        var conflictIssues = FilterEligibleIssues(configuration, inProgressIssues, assignmentIdentity, currentModel);
        var conflict = FindIssueConflict(conflictIssues, configuration);
        if (conflict is not null) return conflict;

        var response = await EvaluateInProgressIssuesAsync(
            configuration,
            inProgressIssues,
            pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, new PullRequestSelection { Number = true, State = true, Labels = true, ClosingIssuesReferences = true }, CancellationToken.None),
            currentModel,
            pullRequestNumber => GitHubCliService.GetIssueByNumberAsync(workingDirectory, pullRequestNumber, CancellationToken.None),
            assignmentIdentity);
        return response;
    }

    public static async Task<WorkflowResponse> EvaluateInProgressIssuesAsync(RouterConfiguration configuration, IReadOnlyList<Issue> inProgressIssues, Func<int, Task<PullRequest>> getPullRequest, string? currentModel = null, Func<int, Task<Issue>>? getIssue = null, AssignmentIdentity? assignmentIdentity = null)
    {
        if (inProgressIssues.Count == 0)
        {
            return new WorkflowResponse
            {
                IsSuccessful = true,
                Message = "No in-progress issues found.",
                ConsideredIssues = inProgressIssues.ToList()
            };
        }

        var tasks = new List<WorkflowItem>();
        foreach (var issue in inProgressIssues)
        {
            if (issue.ClosingPullRequestsReferences.Count > 0)
            {
                var linkedResponse = await CheckIssueLinkedPullRequestsAsync(configuration, new[] { issue }, getPullRequest, getIssue);
                if (!linkedResponse.IsSuccessful)
                {
                    return linkedResponse;
                }

                tasks.AddRange(linkedResponse.Tasks);
                continue;
            }

            tasks.Add(new WorkflowItem
            {
                Type = WorkflowItemType.ResumeInProgressIssue,
                IssueNumber = issue.Number,
                Status = new WorkflowTaskStatus
                {
                    Message = $"Issue #{issue.Number} is already in progress and has no linked pull request. Resume or report the existing work; do not start a new issue."
                }
            });
        }

        var filtered = AssignmentRoutingService.FilterCodingTasks(configuration, assignmentIdentity, inProgressIssues, WorkerRoutingService.FilterCodingTasks(configuration, inProgressIssues, new WorkflowResponse
        {
            IsSuccessful = true,
            Message = "In-progress issue evaluation completed.",
            Tasks = tasks,
            ConsideredIssues = inProgressIssues.ToList()
        }, currentModel));

        var codingIssueNumbers = filtered.Tasks
            .Where(task => task.Type is WorkflowItemType.ChangeRequest or WorkflowItemType.ResumeInProgressIssue)
            .Select(task => task.IssueNumber)
            .Distinct()
            .ToList();
        if (codingIssueNumbers.Count > 1)
        {
            return new WorkflowResponse
            {
                IsSuccessful = false,
                Message = $"Multiple issues are marked as in progress: {string.Join(", ", codingIssueNumbers.Select(number => $"#{number}"))}. Resolve the workflow state before starting new work."
            };
        }

        return filtered;
    }

    public static async Task<WorkflowResponse> CheckNewIssuesAsync(RouterConfiguration configuration, string workingDirectory, int scanLimit = 30, string? currentModel = null, AssignmentIdentity? assignmentIdentity = null)
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

        var discovery = await DiscoverIssuesAsync(configuration, workingDirectory, openIssueFilters, false, scanLimit, currentModel, assignmentIdentity);
        var openIssues = discovery.Issues;
        var conflictIssues = FilterEligibleIssues(configuration, openIssues, assignmentIdentity, currentModel);
        var conflict = FindIssueConflict(conflictIssues, configuration);
        if (conflict is not null) return conflict;

        if (openIssues.Count > 0)
        {
            var workflowTasks = openIssues.Select(issue => new WorkflowItem
            {
                Type = WorkflowItemType.NewIssue,
                IssueNumber = issue.Number
            }).ToList();

            return AssignmentRoutingService.FilterCodingTasks(configuration, assignmentIdentity, openIssues, WorkerRoutingService.FilterCodingTasks(configuration, openIssues, new WorkflowResponse
            {
                Tasks = workflowTasks,
                IsSuccessful = true,
                Message = "New issues found.",
                ConsideredIssues = openIssues.ToList()
            }, currentModel));
        }
        else
        {
            return new WorkflowResponse
            {
                Tasks = new List<WorkflowItem>(),
                IsSuccessful = true,
                Message = "No new issues found.",
                ConsideredIssues = openIssues.ToList()
            };
        }
    }

    public static async Task<WorkflowResponse> CheckRepositoryGateAsync(RouterConfiguration configuration, string workingDirectory)
    {
        var gatedIssues = await RepositoryGateService.GetOpenGatedIssuesAsync(workingDirectory, configuration);
        return await EvaluateRepositoryGateAsync(
            configuration,
            gatedIssues,
            pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(
                workingDirectory,
                pullRequestNumber,
                new PullRequestSelection { Number = true, State = true, Labels = true },
                CancellationToken.None));
    }

    public static async Task<WorkflowResponse> EvaluateRepositoryGateAsync(
        RouterConfiguration configuration,
        IReadOnlyList<Issue> gatedIssues,
        Func<int, Task<PullRequest>> getPullRequest)
    {
        var tasks = new List<WorkflowItem>();

        foreach (var issue in gatedIssues
            .Where(issue => string.Equals(issue.State, "open", StringComparison.OrdinalIgnoreCase) && RepositoryGateService.IsGated(issue, configuration))
            .OrderBy(issue => issue.Number))
        {
            var issueResolution = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States);
            if (issueResolution.IsAmbiguous)
            {
                tasks.Add(CreateGateBlock(issue, $"{issueResolution.DescribeConflict($"gated issue #{issue.Number}")} Remove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work."));
                continue;
            }

            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.Abandoned))
            {
                continue;
            }

            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.Ready))
            {
                tasks.Add(new WorkflowItem { Type = WorkflowItemType.NewIssue, IssueNumber = issue.Number, Status = new WorkflowTaskStatus { Message = $"Repository workflow is gated by issue #{issue.Number}." } });
                continue;
            }

            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.InProgress))
            {
                tasks.AddRange(await EvaluateGatedIssueWorkAsync(configuration, issue, getPullRequest, allowCompletedWithoutPullRequest: false));
                continue;
            }

            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.Completed))
            {
                tasks.AddRange(await EvaluateGatedIssueWorkAsync(configuration, issue, getPullRequest, allowCompletedWithoutPullRequest: true));
                continue;
            }

            if (issueResolution.MatchedLabels.ContainsKey(WorkflowState.Blocked) || issueResolution.MatchedLabels.ContainsKey(WorkflowState.NeedsInfo))
            {
                tasks.Add(CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}, which is blocked or needs information. Remove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work."));
                continue;
            }

            tasks.Add(CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}, which has no actionable workflow state. Remove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work."));
        }

        return new WorkflowResponse
        {
            IsSuccessful = true,
            Tasks = tasks,
            Message = tasks.Count == 0 ? "No blocking repository gates found." : "Repository gate evaluation completed.",
            ConsideredIssues = gatedIssues.ToList()
        };
    }

    private static async Task<List<WorkflowItem>> EvaluateGatedIssueWorkAsync(
        RouterConfiguration configuration,
        Issue issue,
        Func<int, Task<PullRequest>> getPullRequest,
        bool allowCompletedWithoutPullRequest)
    {
        if (issue.ClosingPullRequestsReferences.Count == 0)
        {
            if (allowCompletedWithoutPullRequest)
            {
                return new List<WorkflowItem> { CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}, which has no linked pull request to resolve. Remove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work.") };
            }

            return new List<WorkflowItem> { new() { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = issue.Number, Status = new WorkflowTaskStatus { Message = $"Repository workflow is gated by issue #{issue.Number}." } } };
        }

        var linkedPullRequests = new List<PullRequest>();
        foreach (var reference in issue.ClosingPullRequestsReferences)
        {
            linkedPullRequests.Add(await getPullRequest(reference.Number));
        }

        var openPullRequests = linkedPullRequests.Where(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase)).ToList();
        if (openPullRequests.Count == 0)
        {
            if (!allowCompletedWithoutPullRequest)
            {
                return new List<WorkflowItem> { new() { Type = WorkflowItemType.ResumeInProgressIssue, IssueNumber = issue.Number, Status = new WorkflowTaskStatus { Message = $"Repository workflow is gated by issue #{issue.Number}. Resume the interrupted workstream." } } };
            }

            if (linkedPullRequests.All(pr => pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<WorkflowItem>();
            }

            return new List<WorkflowItem> { CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}, which has no open pull request to resolve. Remove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work.") };
        }

        var conflictingPullRequest = openPullRequests
            .Select(pr => new { PullRequest = pr, Resolution = WorkflowStateResolver.Resolve(pr.Labels.Select(label => label.Name), configuration.PullRequestStates) })
            .FirstOrDefault(entry => entry.Resolution.IsAmbiguous);
        if (conflictingPullRequest is not null)
        {
            return new List<WorkflowItem> { CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}. {conflictingPullRequest.Resolution.DescribeConflict($"pull request #{conflictingPullRequest.PullRequest.Number}")} Remove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work.") };
        }

        var changesRequested = openPullRequests.Where(pr => HasPullRequestState(pr, configuration, PullRequestState.ChangesRequested)).ToList();
        if (changesRequested.Count > 0)
        {
            return changesRequested.Select(pr => new WorkflowItem
            {
                Type = WorkflowItemType.ChangeRequest,
                IssueNumber = issue.Number,
                PullRequestNumber = pr.Number,
                Status = new WorkflowTaskStatus { Message = $"Repository workflow is gated by issue #{issue.Number}. Pull request #{pr.Number} has requested changes." }
            }).ToList();
        }

        var waitingPullRequest = openPullRequests.FirstOrDefault(pr => HasPullRequestState(pr, configuration, PullRequestState.ReviewRequested));
        if (waitingPullRequest is not null)
        {
            return new List<WorkflowItem> { CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}.\nPull request #{waitingPullRequest.Number} is awaiting review.\nRemove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work.") };
        }

        waitingPullRequest = openPullRequests.FirstOrDefault(pr => HasPullRequestState(pr, configuration, PullRequestState.AwaitingMerge));
        if (waitingPullRequest is not null)
        {
            return new List<WorkflowItem> { CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}.\nPull request #{waitingPullRequest.Number} is awaiting merge.\nRemove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work.") };
        }

        if (openPullRequests.All(pr => HasPullRequestState(pr, configuration, PullRequestState.Deferred)))
        {
            var deferred = openPullRequests[0];
            return new List<WorkflowItem> { CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}.\nPull request #{deferred.Number} is deferred.\nRemove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work.") };
        }

        var unknown = openPullRequests[0];
        return new List<WorkflowItem> { CreateGateBlock(issue, $"Repository workflow is gated by issue #{issue.Number}.\nPull request #{unknown.Number} is in an unknown state.\nRemove {RepositoryGateService.FormatGateLabel(configuration)} from issue #{issue.Number} to allow unrelated work.") };
    }

    private static WorkflowItem CreateGateBlock(Issue issue, string message) => new()
    {
        Type = WorkflowItemType.RepositoryGateBlock,
        IssueNumber = issue.Number,
        Status = new WorkflowTaskStatus { Message = message }
    };

    public static async Task<WorkflowResponse> CheckCompletedIssuesAsync(RouterConfiguration configuration, string workingDirectory, int scanLimit = 30, string? currentModel = null, AssignmentIdentity? assignmentIdentity = null)
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

        var discovery = await DiscoverIssuesAsync(configuration, workingDirectory, completedIssueFilters, true, scanLimit, currentModel, assignmentIdentity);
        var completedIssues = discovery.Issues;

        var conflictIssues = FilterEligibleIssues(configuration, completedIssues, assignmentIdentity, currentModel);
        var conflict = FindIssueConflict(conflictIssues, configuration);
        if (conflict is not null) return conflict;

        if (completedIssues.Count > 0)
        {
            var response = await CheckIssueLinkedPullRequestsAsync(
                configuration,
                completedIssues,
                pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, new PullRequestSelection { Number = true, State = true, Labels = true, ClosingIssuesReferences = true }, CancellationToken.None),
                issueNumber => GitHubCliService.GetIssueByNumberAsync(workingDirectory, issueNumber, CancellationToken.None));
            return AssignmentRoutingService.FilterCodingTasks(configuration, assignmentIdentity, completedIssues, WorkerRoutingService.FilterCodingTasks(configuration, completedIssues, response, currentModel));
        }
        else
        {
            return new WorkflowResponse
            {
                Tasks = new List<WorkflowItem>(),
                IsSuccessful = true,
                Message = "No completed issues found.",
                ConsideredIssues = completedIssues.ToList()
            };
        }
    }

    public static async Task<WorkflowResponse> CheckIssueLinkedPullRequestsAsync(RouterConfiguration configuration, string workingDirectory, IEnumerable<Issue> completedIssues)
    {
        return await CheckIssueLinkedPullRequestsAsync(
            configuration,
            completedIssues,
            pullRequestNumber => GitHubCliService.GetPullRequestByNumberAsync(workingDirectory, pullRequestNumber, new PullRequestSelection { Number = true, State = true, Labels = true, ClosingIssuesReferences = true }, CancellationToken.None),
            issueNumber => GitHubCliService.GetIssueByNumberAsync(workingDirectory, issueNumber, CancellationToken.None));
    }

    public static async Task<WorkflowResponse> CheckIssueLinkedPullRequestsAsync(RouterConfiguration configuration, IEnumerable<Issue> completedIssues, Func<int, Task<PullRequest>> getPullRequest, Func<int, Task<Issue>>? getIssue = null)
    {
        var issueList = completedIssues.ToList();
        var workflowTasks = new List<WorkflowItem>();
        var noIssuesWithLinkedPRs = issueList.Where(issue => issue.ClosingPullRequestsReferences.Count == 0).ToList();
        var issuesWithLinkedPRs = issueList.Where(issue => issue.ClosingPullRequestsReferences.Count > 0).ToList();

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

            var hasChangesRequested = prList.Any(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase) && HasPullRequestState(pr, configuration, PullRequestState.ChangesRequested));
            var workerConflict = hasChangesRequested
                ? await FindPullRequestWorkerConflictAsync(configuration, prList.Where(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase) && HasPullRequestState(pr, configuration, PullRequestState.ChangesRequested)).ToList(), issueList, getIssue)
                : null;
            if (workerConflict is not null)
            {
                workflowTasks.Add(new WorkflowItem
                {
                    Type = WorkflowItemType.UnknownPullRequestState,
                    IssueNumber = issue.Number,
                    PullRequestNumber = workerConflict.PullRequestNumber,
                    Status = new WorkflowTaskStatus
                    {
                        Message = workerConflict.Message,
                        LinkedPullRequests = prList.Select(pr => pr.Number).ToList()
                    }
                });
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
            Message = "Workflow check completed successfully.",
            ConsideredIssues = issueList
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

    private static Task<WorkerCandidateDiscoveryResult> DiscoverIssuesAsync(
        RouterConfiguration configuration,
        string workingDirectory,
        IssueFilters filters,
        bool addLinkedPRToSelection,
        int scanLimit,
        string? currentModel,
        AssignmentIdentity? assignmentIdentity)
    {
        async Task<IReadOnlyList<Issue>> FetchPreferredIssuesAsync(int requestedLimit)
        {
            var perUsername = new List<IReadOnlyList<Issue>>();
            foreach (var username in assignmentIdentity!.GitHubUsernames)
            {
                var preferredFilters = new IssueFilters
                {
                    Labels = filters.Labels.ToList(),
                    SearchTerms = filters.SearchTerms.Append($"assignee:{username}").ToList(),
                    Limit = requestedLimit,
                    SortBy = filters.SortBy,
                    SortDirection = filters.SortDirection
                };
                perUsername.Add(await GitHubCliService.GetIssuesAsync(
                    workingDirectory,
                    preferredFilters,
                    addLinkedPRToSelection,
                    CancellationToken.None));
            }

            return MergePreferredIssues(perUsername, filters.SortBy, filters.SortDirection);
        }

        async Task<IReadOnlyList<Issue>> FetchUnassignedIssuesAsync(int requestedLimit)
        {
            var unassignedFilters = new IssueFilters
            {
                Labels = filters.Labels.ToList(),
                SearchTerms = filters.SearchTerms.Append("no:assignee").ToList(),
                Limit = requestedLimit,
                SortBy = filters.SortBy,
                SortDirection = filters.SortDirection
            };
            return await GitHubCliService.GetIssuesAsync(
                workingDirectory,
                unassignedFilters,
                addLinkedPRToSelection,
                CancellationToken.None);
        }

        Func<int, Task<IReadOnlyList<Issue>>>? fetchPreferredIssues = null;
        if (AssignmentRoutingService.RequiresLocalIdentity(configuration) && assignmentIdentity?.GitHubUsernames is { Count: > 0 })
        {
            fetchPreferredIssues = FetchPreferredIssuesAsync;
        }

        Func<int, Task<IReadOnlyList<Issue>>>? fetchUnassignedIssues = null;
        if (AssignmentRoutingService.RequiresLocalIdentity(configuration) &&
            string.Equals(AssignmentRoutingService.GetUnassignedMode(configuration), AssignmentRoutingService.UnassignedAllow, StringComparison.Ordinal))
        {
            fetchUnassignedIssues = FetchUnassignedIssuesAsync;
        }

        return WorkerRoutingService.DiscoverCandidatesAsync(
            configuration,
            scanLimit,
            currentModel,
            async requestedLimit => await GitHubCliService.GetIssuesAsync(
                workingDirectory,
                new IssueFilters
                {
                    Labels = filters.Labels.ToList(),
                    SearchTerms = filters.SearchTerms.ToList(),
                    Limit = requestedLimit,
                    SortBy = filters.SortBy,
                    SortDirection = filters.SortDirection
                },
                addLinkedPRToSelection,
                CancellationToken.None),
            assignmentIdentity,
            fetchPreferredIssues,
            fetchUnassignedIssues);
    }

    public static IReadOnlyList<Issue> MergePreferredIssues(
        IReadOnlyList<IReadOnlyList<Issue>> perUsernameIssues,
        IssueSortField? sortBy,
        SortDirection? sortDirection)
    {
        var byNumber = new Dictionary<int, Issue>();
        foreach (var issues in perUsernameIssues)
        {
            foreach (var issue in issues)
            {
                byNumber[issue.Number] = issue;
            }
        }

        return OrderBySortField(byNumber.Values, sortBy ?? IssueSortField.CreatedAt, sortDirection ?? SortDirection.Ascending).ToList();
    }

    private static IOrderedEnumerable<Issue> OrderBySortField(IEnumerable<Issue> issues, IssueSortField sortBy, SortDirection sortDirection) =>
        (sortBy, sortDirection) switch
        {
            (IssueSortField.CreatedAt, SortDirection.Ascending) => issues.OrderBy(issue => issue.CreatedAt),
            (IssueSortField.CreatedAt, SortDirection.Descending) => issues.OrderByDescending(issue => issue.CreatedAt),
            (IssueSortField.UpdatedAt, SortDirection.Ascending) => issues.OrderBy(issue => issue.UpdatedAt),
            (IssueSortField.UpdatedAt, SortDirection.Descending) => issues.OrderByDescending(issue => issue.UpdatedAt),
            _ => issues.OrderBy(issue => issue.CreatedAt)
        };

    private static List<Issue> FilterEligibleIssues(RouterConfiguration configuration, IReadOnlyList<Issue> issues, AssignmentIdentity? assignmentIdentity, string? currentModel)
    {
        var workerEligible = WorkerRoutingService.IsEnabled(configuration)
            ? WorkerRoutingService.FilterIssues(configuration, issues, currentModel).EligibleIssues.ToList()
            : issues.ToList();

        return AssignmentRoutingService.IsEnabled(configuration)
            ? AssignmentRoutingService.FilterIssues(configuration, assignmentIdentity, workerEligible).EligibleIssues.ToList()
            : workerEligible;
    }

    private static async Task<PullRequestWorkerConflict?> FindPullRequestWorkerConflictAsync(
        RouterConfiguration configuration,
        IReadOnlyList<PullRequest> pullRequests,
        IReadOnlyList<Issue> knownIssues,
        Func<int, Task<Issue>>? getIssue)
    {
        if (!WorkerRoutingService.IsEnabled(configuration))
        {
            return null;
        }

        foreach (var pullRequest in pullRequests.Where(pr => pr.State.Equals("open", StringComparison.OrdinalIgnoreCase)))
        {
            if (pullRequest.ClosingIssuesReferences.Count == 0)
            {
                continue;
            }

            var closingIssues = new List<Issue>();
            foreach (var reference in pullRequest.ClosingIssuesReferences)
            {
                var issue = knownIssues.FirstOrDefault(candidate => candidate.Number == reference.Number);
                if (issue is null && getIssue is not null)
                {
                    issue = await getIssue(reference.Number);
                }

                if (issue is not null)
                {
                    closingIssues.Add(issue);
                }
            }

            var resolutions = closingIssues
                .Select(issue => new { Issue = issue, Resolution = WorkerRoutingService.ResolveIssueWorker(configuration, issue) })
                .ToList();
            var invalid = resolutions.FirstOrDefault(entry => !entry.Resolution.IsEligible);
            if (invalid is not null)
            {
                return new PullRequestWorkerConflict(
                    pullRequest.Number,
                    $"Pull request #{pullRequest.Number} closes issue #{invalid.Issue.Number}, but its worker assignment is invalid. {invalid.Resolution.Message}");
            }

            var workers = resolutions
                .Select(entry => entry.Resolution.WorkerProfile)
                .Where(worker => !string.IsNullOrWhiteSpace(worker))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (workers.Count > 1)
            {
                return new PullRequestWorkerConflict(
                    pullRequest.Number,
                    $"Pull request #{pullRequest.Number} closes issues assigned to conflicting workers: {string.Join(", ", workers)}. Keep all closing issues on the same worker before continuing change-request work.");
            }
        }

        return null;
    }

    private static WorkflowResponse? FindIssueConflict(IEnumerable<Issue> issues, RouterConfiguration configuration)
    {
        var conflict = issues.Select(issue => new { Issue = issue, Resolution = WorkflowStateResolver.Resolve(issue.Labels.Select(label => label.Name), configuration.States) }).FirstOrDefault(entry => entry.Resolution.IsAmbiguous);
        return conflict is null ? null : new WorkflowResponse { IsSuccessful = false, Message = conflict.Resolution.DescribeConflict($"issue #{conflict.Issue.Number}") };
    }

    private sealed record PullRequestWorkerConflict(int PullRequestNumber, string Message);
}
