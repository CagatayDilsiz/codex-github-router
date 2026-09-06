using CodexGithubRouter.Work;
using CodexGithubRouter.Workflow;

namespace CodexGithubRouter.Diagnostics;

public sealed class HookDiagnosticScope
{
    private const int TruncatedLength = 500;
    private const string SafeRepositoryResolutionError = "Not a valid Git repository.";

    private readonly Guid _invocationId = Guid.NewGuid();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly string? _workingDirectory;
    private readonly Func<string, Task<string?>> _resolveGitCommonDirectory;
    private readonly Func<string?, Task<DiagnosticsPolicy?>> _resolveDiagnosticsPolicy;

    private bool _enabled = true;
    private int _retentionDays = 7;
    private bool _policyAppliedFromConfig;
    private string? _gitCommonDirectory;
    private string? _worktreeIdentity;
    private string? _activationMode;
    private bool? _activationResult;
    private bool? _autonomousEnabled;
    private string? _workflowItemType;
    private int? _issueNumber;
    private int? _pullRequestNumber;
    private string? _worker;
    private string? _model;
    private string? _identity;
    private string? _claimId;
    private string _result = "bypass";
    private string? _blockReason;
    private string? _errorType;
    private string? _errorMessage;

    public Guid InvocationId => _invocationId;

    public HookDiagnosticScope(
        string? workingDirectory,
        Func<string, Task<string?>> resolveGitCommonDirectory,
        Func<string?, Task<DiagnosticsPolicy?>>? resolveDiagnosticsPolicy = null,
        string? model = null)
    {
        _workingDirectory = workingDirectory;
        _resolveGitCommonDirectory = resolveGitCommonDirectory;
        _resolveDiagnosticsPolicy = resolveDiagnosticsPolicy ?? (_ => Task.FromResult<DiagnosticsPolicy?>(null));
        _model = model;
    }

    public void SetDiagnosticsPolicy(DiagnosticsPolicy policy)
    {
        _enabled = policy.Enabled;
        _retentionDays = policy.RetentionDays;
        _policyAppliedFromConfig = true;
    }

    public void SetAutonomous(bool enabled) => _autonomousEnabled = enabled;

    public void SetActivation(string? mode, bool result)
    {
        _activationMode = mode;
        _activationResult = result;
    }

    public void SetRepository(string gitCommonDirectory) => _gitCommonDirectory = gitCommonDirectory;

    public void SetWorktree(string worktreeIdentity) => _worktreeIdentity = worktreeIdentity;

    public void SetClaim(WorkClaim? claim)
    {
        if (claim is null)
        {
            return;
        }

        _claimId = claim.ClaimId == Guid.Empty ? null : claim.ClaimId.ToString("N")[..8];
        _worker = string.IsNullOrWhiteSpace(claim.WorkerProfile) ? _worker : claim.WorkerProfile;
        _model = string.IsNullOrWhiteSpace(claim.Model) ? _model : claim.Model;
        _worktreeIdentity = string.IsNullOrWhiteSpace(claim.WorktreeId) ? _worktreeIdentity : claim.WorktreeId;
    }

    public void SetIdentity(string? identity) => _identity = string.IsNullOrWhiteSpace(identity) ? null : identity;

    public void SetSelectedTask(WorkflowItem? task)
    {
        if (task is null)
        {
            return;
        }

        _workflowItemType = task.Type.ToString();
        _issueNumber = task.IssueNumber;
        _pullRequestNumber = task.PullRequestNumber;
    }

    public void Bypass() => _result = "bypass";

    public void Block(string? reason)
    {
        _result = "block";
        _blockReason = Truncate(reason);
    }

    public void Context(WorkflowItem? task)
    {
        SetSelectedTask(task);
        _result = "context";
    }

    public void Error(Exception exception)
    {
        _result = "error";
        _errorType = exception.GetType().Name;
        _errorMessage = IsSafeErrorMessage(exception) ? Truncate(exception.Message) : null;
    }

    public async Task CompleteAsync()
    {
        try
        {
            if (!_policyAppliedFromConfig)
            {
                var policy = await _resolveDiagnosticsPolicy(_workingDirectory);
                if (policy is not null)
                {
                    SetDiagnosticsPolicy(policy);
                }
            }

            if (!_enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_gitCommonDirectory) && !string.IsNullOrWhiteSpace(_workingDirectory))
            {
                _gitCommonDirectory = await _resolveGitCommonDirectory(_workingDirectory);
            }

            if (string.IsNullOrWhiteSpace(_gitCommonDirectory))
            {
                return;
            }

            var diagnosticEvent = new HookDiagnosticEvent
            {
                EventName = "hook.invocation",
                InvocationId = _invocationId,
                TimestampUtc = DateTimeOffset.UtcNow,
                DurationMs = (long)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds,
                RepositoryIdentity = _gitCommonDirectory,
                WorktreeIdentity = _worktreeIdentity,
                AutonomousEnabled = _autonomousEnabled,
                ActivationMode = _activationMode,
                ActivationResult = _activationResult,
                WorkflowItemType = _workflowItemType,
                IssueNumber = _issueNumber,
                PullRequestNumber = _pullRequestNumber,
                Worker = _worker,
                Model = _model,
                Identity = _identity,
                ClaimId = _claimId,
                Result = _result,
                BlockReason = _blockReason,
                ErrorType = _errorType,
                ErrorMessage = _errorMessage
            };

            await HookDiagnosticStore.WriteAsync(_gitCommonDirectory, diagnosticEvent);
            await HookDiagnosticStore.PruneAsync(_gitCommonDirectory, _retentionDays);
        }
        catch (Exception)
        {
            // Best-effort diagnostics must never change hook behavior or exit codes.
        }
    }

    private static bool IsSafeErrorMessage(Exception exception)
    {
        if (exception is WorkClaimFileException)
        {
            return true;
        }

        return string.Equals(exception.Message, SafeRepositoryResolutionError, StringComparison.Ordinal);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= TruncatedLength)
        {
            return value;
        }

        return value[..TruncatedLength] + "...";
    }
}
