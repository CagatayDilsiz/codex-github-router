using CodexGithubRouter.GitHub;
using CodexGithubRouter.Work;

namespace CodexGithubRouter.Workflow;

/// <summary>
/// Shared decision model for PR-review routing (#52). Review work is PR-native: a pull request
/// does not need to close an issue to be reviewable, and review eligibility is driven by GitHub's
/// direct requested-reviewer signal authenticated through <c>gh</c>. CGR never invents a second
/// reviewer-assignment model on top of GitHub.
/// </summary>
public static class ReviewRoutingService
{
    public static bool IsEnabled(RouterConfiguration configuration) =>
        configuration.Policies.ReviewRouting?.Enabled == true;

    public static PullRequestSelection Selection => PullRequestSelection.ReviewSelection();

    /// <summary>
    /// Evaluates whether the given requested-reviewer login can produce claimable
    /// <see cref="WorkflowItemType.PullRequestReview"/> work for the pull request. This is the same
    /// decision used by discovery, claimed-work continuation, reconciliation and the diagnostic
    /// explanation (<c>cgr explain --pr</c>), so read-only output always matches production routing.
    /// </summary>
    public static IReadOnlyList<RoutingStage> EvaluateStages(
        RouterConfiguration configuration,
        PullRequest pullRequest,
        string reviewerLogin,
        WorkClaim? activeClaim,
        IReadOnlyList<WorkClaim>? otherClaims,
        AssignmentIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(pullRequest);
        if (string.IsNullOrWhiteSpace(reviewerLogin))
        {
            throw new ArgumentException("A reviewer login is required to evaluate review work.", nameof(reviewerLogin));
        }

        var stages = new List<RoutingStage>
        {
            EvaluatePullRequestState(configuration, pullRequest),
            EvaluateReviewRequest(configuration, pullRequest, reviewerLogin),
            EvaluateAuthenticationIdentity(configuration, pullRequest, reviewerLogin, identity),
            EvaluateDraft(pullRequest),
            EvaluateReviewerAuthor(pullRequest, reviewerLogin),
            EvaluateCgrState(configuration, pullRequest),
            EvaluateClaims(pullRequest, reviewerLogin, activeClaim, otherClaims ?? Array.Empty<WorkClaim>())
        };

        return stages;
    }

    public static bool IsEligible(IEnumerable<RoutingStage> stages) =>
        stages.All(stage => stage.Verdict != RoutingVerdict.HardIneligible);

    /// <summary>
    /// True when the claimed review cycle is still the current GitHub review state. The cycle
    /// marker is the latest submitted-review node ID captured at claim acquisition: a submit (even
    /// followed by a fast re-request) produces a fresh review identity, so the stored marker no
    /// longer matches and the old claim is treated as completed. A legacy claim without a stored
    /// marker is kept conservatively while the reviewer is still requested.
    /// </summary>
    public static bool IsReviewCycleCurrent(PullRequest pullRequest, string reviewerLogin, WorkClaim claim)
    {
        if (!pullRequest.IsDirectlyReviewerRequested(reviewerLogin))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(claim.ReviewCycleId))
        {
            return true;
        }

        var currentReviewId = pullRequest.GetLatestReviewId(reviewerLogin);
        return !string.IsNullOrWhiteSpace(currentReviewId) &&
            string.Equals(currentReviewId, claim.ReviewCycleId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Release decision for an existing review claim is owned by
    /// <see cref="WorkflowService.EvaluateClaimedReviewWork"/>: known terminal (merged/closed),
    /// draft, removed-reviewer, stale-cycle and contradictory CGR states produce the release-candidate
    /// sentinel, while an <em>unknown</em> GitHub pull-request state or an ambiguous CGR state fails
    /// closed (no release, no review work). Reconciliation maps that decision through
    /// <see cref="WorkClaimReconciliationService.DetermineReviewAsync"/> to the fail-closed
    /// <see cref="WorkClaimReconciliationRecommendation.UnableToDetermine"/>.
    /// </summary>
    public static bool IsUnknownState(PullRequest pullRequest) =>
        !IsOpen(pullRequest) && !IsTerminal(pullRequest);

    public static bool IsOpen(PullRequest pullRequest) =>
        string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase);

    public static bool IsTerminal(PullRequest pullRequest) =>
        string.Equals(pullRequest.State, "merged", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase);

    public static string ReviewerIdentityLabel(AssignmentIdentity? identity, string authenticatedLogin)
    {
        var aliases = identity?.GitHubUsernames
            .Where(login => !string.Equals(login, authenticatedLogin, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<string>();
        return aliases.Count == 0
            ? $"'{authenticatedLogin}'"
            : $"'{authenticatedLogin}' (local identity aliases: {string.Join(", ", aliases)})";
    }

    public static bool ConflictsWithReviewClaims(IReadOnlyList<WorkClaim> claims, int pullRequestNumber, string reviewerLogin) =>
        claims.Any(claim =>
            claim.WorkType == WorkClaimType.Review &&
            claim.PullRequestNumber == pullRequestNumber &&
            string.Equals(claim.ReviewerLogin, reviewerLogin, StringComparison.OrdinalIgnoreCase));

    private static RoutingStage EvaluatePullRequestState(RouterConfiguration configuration, PullRequest pullRequest)
    {
        if (string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase))
        {
            return new RoutingStage
            {
                Name = "Pull Request State",
                Verdict = RoutingVerdict.Pass,
                Message = $"Pull request #{pullRequest.Number} is open."
            };
        }

        if (string.Equals(pullRequest.State, "merged", StringComparison.OrdinalIgnoreCase))
        {
            return new RoutingStage
            {
                Name = "Pull Request State",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"Pull request #{pullRequest.Number} is merged and cannot produce review work."
            };
        }

        if (string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return new RoutingStage
            {
                Name = "Pull Request State",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"Pull request #{pullRequest.Number} is closed and cannot produce review work."
            };
        }

        return new RoutingStage
        {
            Name = "Pull Request State",
            Verdict = RoutingVerdict.HardIneligible,
            Message = $"Pull request #{pullRequest.Number} is in an unknown state '{pullRequest.State}'."
        };
    }

    private static RoutingStage EvaluateReviewRequest(RouterConfiguration configuration, PullRequest pullRequest, string reviewerLogin)
    {
        if (!IsEnabled(configuration))
        {
            return new RoutingStage
            {
                Name = "Review Routing / Requested Reviewer",
                Verdict = RoutingVerdict.HardIneligible,
                Message = "Review routing is disabled (policies.reviewRouting.enabled is false). Pull-request review is not claimable work."
            };
        }

        if (pullRequest.IsDirectlyReviewerRequested(reviewerLogin))
        {
            var requestId = pullRequest.GetReviewRequestId(reviewerLogin);
            return new RoutingStage
            {
                Name = "Review Routing / Requested Reviewer",
                Verdict = RoutingVerdict.Pass,
                Message = requestId is null
                    ? $"GitHub directly requests '{reviewerLogin}' to review pull request #{pullRequest.Number}."
                    : $"GitHub directly requests '{reviewerLogin}' to review pull request #{pullRequest.Number} (review request {requestId})."
            };
        }

        var teamSlugs = pullRequest.RequestedTeamReviewerSlugs;
        var teamPart = teamSlugs.Count > 0
            ? $" The pull request has a team review request for {string.Join(", ", teamSlugs.Select(slug => $"@{slug}") )}; team membership is not resolvable deterministically and is not claimable in this issue."
            : string.Empty;
        return new RoutingStage
        {
            Name = "Review Routing / Requested Reviewer",
            Verdict = RoutingVerdict.HardIneligible,
            Message = $"'{reviewerLogin}' is not a directly requested reviewer of pull request #{pullRequest.Number}.{teamPart}"
        };
    }

    private static RoutingStage EvaluateAuthenticationIdentity(RouterConfiguration configuration, PullRequest pullRequest, string reviewerLogin, AssignmentIdentity? identity)
    {
        if (identity?.GitHubUsernames is { Count: > 0 })
        {
            var isConsistent = identity.GitHubUsernames.Any(login => string.Equals(login.Trim(), reviewerLogin, StringComparison.OrdinalIgnoreCase));
            if (!isConsistent)
            {
                return new RoutingStage
                {
                    Name = "GitHub Authentication / Local Identity",
                    Verdict = RoutingVerdict.HardIneligible,
                    Message = $"The authenticated gh account '{reviewerLogin}' does not match the configured local identity ({string.Join(", ", identity.GitHubUsernames)}). CGR never acts as a different GitHub account; reconcile the local identity or the authenticated gh account."
                };
            }

            return new RoutingStage
            {
                Name = "GitHub Authentication / Local Identity",
                Verdict = RoutingVerdict.Pass,
                Message = $"The authenticated gh account '{reviewerLogin}' is a configured login of the local identity. CGR never acts as a different GitHub account."
            };
        }

        return new RoutingStage
        {
            Name = "GitHub Authentication / Local Identity",
            Verdict = RoutingVerdict.Pass,
            Message = $"The authenticated gh account '{reviewerLogin}' is the review-work identity. CGR never acts as a different GitHub account."
        };
    }

    private static RoutingStage EvaluateDraft(PullRequest pullRequest)
    {
        return !pullRequest.IsDraft
            ? new RoutingStage
            {
                Name = "Draft Check",
                Verdict = RoutingVerdict.Pass,
                Message = $"Pull request #{pullRequest.Number} is not a draft."
            }
            : new RoutingStage
            {
                Name = "Draft Check",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"Pull request #{pullRequest.Number} is a draft and cannot produce review work."
            };
    }

    private static RoutingStage EvaluateReviewerAuthor(PullRequest pullRequest, string reviewerLogin)
    {
        var authorLogin = pullRequest.Author.Login;
        if (string.Equals(authorLogin, reviewerLogin, StringComparison.OrdinalIgnoreCase))
        {
            return new RoutingStage
            {
                Name = "Reviewer / Author",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"'{reviewerLogin}' is the author of pull request #{pullRequest.Number} and cannot review their own pull request."
            };
        }

        return new RoutingStage
        {
            Name = "Reviewer / Author",
            Verdict = RoutingVerdict.Pass,
            Message = string.IsNullOrWhiteSpace(authorLogin)
                ? $"Pull request #{pullRequest.Number} has no resolvable author; the reviewer is not the author."
                : $"Reviewer '{reviewerLogin}' is not the author ('{authorLogin}') of pull request #{pullRequest.Number}."
        };
    }

    private static RoutingStage EvaluateCgrState(RouterConfiguration configuration, PullRequest pullRequest)
    {
        var resolution = WorkflowStateResolver.Resolve(pullRequest.Labels.Select(label => label.Name), configuration.PullRequestStates);
        if (resolution.IsAmbiguous)
        {
            return new RoutingStage
            {
                Name = "CGR PR State Compatibility",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"Pull request #{pullRequest.Number} has conflicting CGR pull-request state labels: {resolution.DescribeConflict($"pull request #{pullRequest.Number}")}. Review work is not claimable until the state is unambiguous."
            };
        }

        if (resolution.MatchedLabels.Count == 0)
        {
            return new RoutingStage
            {
                Name = "CGR PR State Compatibility",
                Verdict = RoutingVerdict.Pass,
                Message = $"Pull request #{pullRequest.Number} has no explicit CGR pull-request state; no contradictory state blocks review work."
            };
        }

        var matchedState = resolution.MatchedLabels.Keys.First();
        var labelList = string.Join(", ", resolution.MatchedLabels[matchedState].OrderBy(label => label, StringComparer.OrdinalIgnoreCase));
        if (matchedState is PullRequestState.ChangesRequested or PullRequestState.AwaitingMerge or PullRequestState.Deferred)
        {
            return new RoutingStage
            {
                Name = "CGR PR State Compatibility",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"Pull request #{pullRequest.Number} has CGR state {matchedState} (labels: {labelList}); review work is contradictory/passive and not claimable."
            };
        }

        return new RoutingStage
        {
            Name = "CGR PR State Compatibility",
            Verdict = RoutingVerdict.Pass,
            Message = $"Pull request #{pullRequest.Number} has compatible CGR pull-request state {matchedState} (labels: {labelList})."
        };
    }

    private static RoutingStage EvaluateClaims(PullRequest pullRequest, string reviewerLogin, WorkClaim? activeClaim, IReadOnlyList<WorkClaim> otherClaims)
    {
        if (activeClaim is not null &&
            activeClaim.WorkType == WorkClaimType.Review &&
            activeClaim.PullRequestNumber == pullRequest.Number &&
            string.Equals(activeClaim.ReviewerLogin, reviewerLogin, StringComparison.OrdinalIgnoreCase))
        {
            return new RoutingStage
            {
                Name = "Work Claim / Other Worktree Claims",
                Verdict = RoutingVerdict.SoftPrefer,
                Message = IsReviewCycleCurrent(pullRequest, reviewerLogin, activeClaim)
                    ? $"This worktree already owns the review claim for (pull request #{pullRequest.Number}, reviewer '{reviewerLogin}'), and it is the current review cycle."
                    : $"This worktree owns a review claim for (pull request #{pullRequest.Number}, reviewer '{reviewerLogin}') on an older review cycle; the claim is releasable so a fresh cycle can be claimed."
            };
        }

        if (ConflictsWithReviewClaims(otherClaims, pullRequest.Number, reviewerLogin))
        {
            return new RoutingStage
            {
                Name = "Work Claim / Other Worktree Claims",
                Verdict = RoutingVerdict.HardIneligible,
                Message = $"(pull request #{pullRequest.Number}, reviewer '{reviewerLogin}') is claimed by another Git worktree and is not available to this worktree."
            };
        }

        return new RoutingStage
        {
            Name = "Work Claim / Other Worktree Claims",
            Verdict = RoutingVerdict.Pass,
            Message = $"No worktree claim blocks review work for (pull request #{pullRequest.Number}, reviewer '{reviewerLogin}')."
        };
    }
}
