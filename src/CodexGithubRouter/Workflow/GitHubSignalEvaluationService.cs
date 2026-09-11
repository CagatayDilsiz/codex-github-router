using CodexGithubRouter.GitHub;

namespace CodexGithubRouter.Workflow;

/// <summary>
/// Native review decision of a pull request as reported by GitHub (<c>reviewDecision</c>).
/// <c>None</c> is the fail-conservative value for an absent or unreadable decision.
/// </summary>
public enum NativeReviewState
{
    None,
    ReviewRequired,
    Approved,
    ChangesRequested
}

/// <summary>
/// Aggregate check state derived from the pull-request status check rollup. <c>Pending</c> means at
/// least one check has not completed; <c>Failed</c> means a completed check failed; <c>Passed</c>
/// means every check completed without failure. <c>Unknown</c> is the fail-conservative value when
/// there is no verifiable check data.
/// </summary>
public enum NativeCheckState
{
    Unknown,
    Pending,
    Failed,
    Passed
}

/// <summary>
/// Native mergeability of a pull request as reported by GitHub (<c>mergeable</c>).
/// <c>Unknown</c> is the fail-conservative value for an unverifiable or not-yet-computed state and
/// never advances work.
/// </summary>
public enum NativeMergeState
{
    Unknown,
    NotMergeable,
    Mergeable
}

/// <summary>
/// The deterministic, centralized model of the GitHub-native signals that participate in workflow
/// evaluation. Produced by <see cref="GitHubSignalEvaluationService.Evaluate"/> and consumed by the
/// hook, the routing plan, and the read-only explanations, so every execution surface uses the same
/// signal interpretation.
/// </summary>
public sealed class PullRequestSignals
{
    public NativeReviewState ReviewState { get; init; } = NativeReviewState.None;

    public NativeCheckState CheckState { get; init; } = NativeCheckState.Unknown;

    public NativeMergeState MergeState { get; init; } = NativeMergeState.Unknown;

    /// <summary>
    /// True when at least one native signal is available. A pull request with no verifiable signal
    /// carries no native evidence and never changes a label-driven decision.
    /// </summary>
    public bool HasSignalData =>
        ReviewState != NativeReviewState.None ||
        CheckState != NativeCheckState.Unknown ||
        MergeState != NativeMergeState.Unknown;
}

/// <summary>
/// Centralized evaluation of GitHub-native review, check and mergeability signals. All routing
/// surfaces (the hook, <c>cgr explain</c> and future daemon polling) share these exact rules so a
/// decision is deterministic and observable through the routing explanation (#50).
///
/// <para>
/// Precedence is documented in <c>docs/configuration.md</c> and implemented by the call sites: the
/// structural GitHub pull-request state (open/closed/merged and draft) always applies; unambiguous
/// CGR workflow labels intentionally override evaluative native signals (backward compatibility);
/// and native signals fill in only when no CGR pull-request state label is present.
/// </para>
///
/// <para>
/// The service is read-only and fail-conservative: pending checks, unknown mergeability and
/// unverifiable check data never produce an actionable transition that advances work.
/// </para>
/// </summary>
public static class GitHubSignalEvaluationService
{
    /// <summary>
    /// Marker that production workflow tasks carry when their state was classified from native
    /// GitHub signals (no CGR workflow label was present). <see cref="WorkflowService"/> writes it
    /// into routing-task messages and <see cref="RoutingExplanationService"/> detects it so the
    /// read-only explanation can deterministically attribute a task to native signal classification.
    /// </summary>
    public const string NoLabelNativeClassificationMarker = "no workflow label is present; the state was classified by native GitHub signals";

    public static bool IsEnabled(RouterConfiguration configuration) =>
        configuration?.Policies.NativeSignals?.Enabled == true;

    /// <summary>
    /// Evaluates the native signals of a single pull request deterministically. Never throws on
    /// signal data; absent or malformed values resolve to the fail-conservative <c>Unknown</c>/<c>None</c>
    /// states so routing never invents evidence.
    /// </summary>
    public static PullRequestSignals Evaluate(PullRequest pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);

        return new PullRequestSignals
        {
            ReviewState = EvaluateReviewDecision(pullRequest.ReviewDecision),
            CheckState = EvaluateCheckRollup(pullRequest.StatusCheckRollup),
            MergeState = EvaluateMergeable(pullRequest.Mergeable)
        };
    }

    /// <summary>
    /// Classifies an open, non-draft pull request from its native signals into the workflow item
    /// type production would route, or <c>null</c> when native signals are disabled, the pull
    /// request is not in an open-and-reviewable state, or no native signal data exists (the caller
    /// keeps its label-only fallback). This is the single shared decision rule for hook, plan and
    /// explanation.
    /// </summary>
    public static WorkflowItemType? ClassifyWorkflowType(RouterConfiguration configuration, PullRequest pullRequest)
    {
        if (!IsEnabled(configuration))
        {
            return null;
        }

        if (!string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase) || pullRequest.IsDraft)
        {
            return null;
        }

        var signals = Evaluate(pullRequest);
        if (!signals.HasSignalData)
        {
            return null;
        }

        return Classify(signals);
    }

    /// <summary>
    /// Builds the workflow item a native-signal classification produces. Returns <c>null</c> when
    /// <see cref="ClassifyWorkflowType"/> returns <c>null</c>.
    /// </summary>
    public static WorkflowItem? CreateWorkflowItem(RouterConfiguration configuration, PullRequest pullRequest, int? issueNumber = null, string? messageSuffix = null)
    {
        var type = ClassifyWorkflowType(configuration, pullRequest);
        if (type is null)
        {
            return null;
        }

        var signals = Evaluate(pullRequest);
        var message = Describe(type.Value, signals, pullRequest.Number);
        if (!string.IsNullOrWhiteSpace(messageSuffix))
        {
            message += " " + messageSuffix;
        }

        return new WorkflowItem
        {
            Type = type.Value,
            IssueNumber = issueNumber,
            PullRequestNumber = pullRequest.Number,
            Status = new WorkflowTaskStatus { Message = message }
        };
    }

    private static WorkflowItemType Classify(PullRequestSignals signals)
    {
        // Deterministic precedence, from most directive to most conservative:
        // 1. GitHub asks the author for changes.
        // 2. A completed check failed — the author must act (fix checks / rerun).
        // 3. A check is still pending — work has not advanced; stay passive.
        // 4. GitHub still requires review — stay passive.
        // 5. All checks passed and the pull request is mergeable — awaiting merge (passive).
        // 6. All checks passed but mergeability is blocked — the author must resolve conflicts.
        // 7. Any other defined-but-unconfirmable state — stay passive; never advance.
        if (signals.ReviewState == NativeReviewState.ChangesRequested)
        {
            return WorkflowItemType.ChangeRequest;
        }

        if (signals.CheckState == NativeCheckState.Failed)
        {
            return WorkflowItemType.ChangeRequest;
        }

        if (signals.CheckState == NativeCheckState.Pending)
        {
            return WorkflowItemType.AwaitingReview;
        }

        if (signals.ReviewState == NativeReviewState.ReviewRequired)
        {
            return WorkflowItemType.AwaitingReview;
        }

        if (signals.CheckState == NativeCheckState.Passed && signals.MergeState == NativeMergeState.Mergeable)
        {
            return WorkflowItemType.AwaitingMerge;
        }

        if (signals.CheckState == NativeCheckState.Passed && signals.MergeState == NativeMergeState.NotMergeable)
        {
            return WorkflowItemType.ChangeRequest;
        }

        return WorkflowItemType.AwaitingReview;
    }

    private static NativeReviewState EvaluateReviewDecision(string reviewDecision)
    {
        if (string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return NativeReviewState.Approved;
        }

        if (string.Equals(reviewDecision, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase))
        {
            return NativeReviewState.ChangesRequested;
        }

        if (string.Equals(reviewDecision, "REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            return NativeReviewState.ReviewRequired;
        }

        return NativeReviewState.None;
    }

    private static NativeCheckState EvaluateCheckRollup(IReadOnlyList<CheckRun> checks)
    {
        if (checks is null || checks.Count == 0)
        {
            return NativeCheckState.Unknown;
        }

        // Pending dominates the whole rollup: an unfinished run means the pull request has not
        // advanced, regardless of what other checks report.
        foreach (var check in checks)
        {
            if (IsPending(check))
            {
                return NativeCheckState.Pending;
            }
        }

        var anyCompleted = false;
        foreach (var check in checks)
        {
            if (IsFailed(check))
            {
                return NativeCheckState.Failed;
            }

            anyCompleted = true;
        }

        // Every entry completed without a failure conclusion. An entry with no verifiable state is
        // classified pending (see IsPending), so reaching here only happens with coherent data.
        return anyCompleted ? NativeCheckState.Passed : NativeCheckState.Unknown;
    }

    /// <summary>
    /// A check is pending when it has not completed or its conclusion is not verifiable. Queued,
    /// in-progress, and succeeded-without-a-conclusion runs keep the pull request in progress so
    /// pending work is never rushed.
    /// </summary>
    private static bool IsPending(CheckRun check) =>
        !string.Equals(check.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(check.Conclusion);

    /// <summary>
    /// A completed check fails on any non-success conclusion that requires attention. Neutral,
    /// cancelled and skipped runs are informational and do not fail the pull request.
    /// </summary>
    private static bool IsFailed(CheckRun check) =>
        string.Equals(check.Conclusion, "FAILURE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(check.Conclusion, "TIMED_OUT", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(check.Conclusion, "ACTION_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(check.Conclusion, "ERROR", StringComparison.OrdinalIgnoreCase);

    private static NativeMergeState EvaluateMergeable(string mergeable)
    {
        if (string.Equals(mergeable, "MERGEABLE", StringComparison.OrdinalIgnoreCase))
        {
            return NativeMergeState.Mergeable;
        }

        if (string.Equals(mergeable, "UNMERGEABLE", StringComparison.OrdinalIgnoreCase))
        {
            return NativeMergeState.NotMergeable;
        }

        return NativeMergeState.Unknown;
    }

    private static string Describe(WorkflowItemType type, PullRequestSignals signals, int pullRequestNumber)
    {
        if (type == WorkflowItemType.ChangeRequest)
        {
            if (signals.ReviewState == NativeReviewState.ChangesRequested)
            {
                return $"GitHub review decision for pull request #{pullRequestNumber} is changes requested; address the requested changes.";
            }

            if (signals.CheckState == NativeCheckState.Failed)
            {
                return $"GitHub checks on pull request #{pullRequestNumber} have failed; fix the failing checks before continuing.";
            }

            if (signals.MergeState == NativeMergeState.NotMergeable)
            {
                return $"Pull request #{pullRequestNumber} has passed checks but is not mergeable; resolve the merge conflicts before continuing.";
            }
        }

        if (type == WorkflowItemType.AwaitingReview)
        {
            if (signals.CheckState == NativeCheckState.Pending)
            {
                return $"Pull request #{pullRequestNumber} has pending GitHub checks and has not advanced; it is still in progress.";
            }

            if (signals.ReviewState == NativeReviewState.ReviewRequired)
            {
                return $"Pull request #{pullRequestNumber} still requires review.";
            }

            return $"Pull request #{pullRequestNumber} native state is not confirmable (checks or mergeability are unknown or pending); no work is advanced.";
        }

        if (type == WorkflowItemType.AwaitingMerge)
        {
            return $"Pull request #{pullRequestNumber} has passed GitHub checks and is mergeable; it is awaiting merge.";
        }

        return $"Pull request #{pullRequestNumber} native signal classification is {type}.";
    }
}