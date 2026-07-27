using System.Text.Json;

namespace CodexGithubRouter.Work;

public static class WorkClaimStore
{
    private const string ClaimFileName = "codex-github-router.work.json";
    private const string LockFileName = "codex-github-router.work.lock";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Task<WorkClaim?> ReadAsync(string gitCommonDirectory, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, () => ReadUnsafeAsync(gitCommonDirectory, cancellationToken), cancellationToken);

    public static Task<WorkClaimAcquisitionResult> TryAcquireAsync(string gitCommonDirectory, WorkClaim requested, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var existing = await ReadUnsafeAsync(gitCommonDirectory, cancellationToken);
            if (existing is not null && !string.Equals(existing.OwnerSessionId, requested.OwnerSessionId, StringComparison.Ordinal))
            {
                return new WorkClaimAcquisitionResult
                {
                    Claim = existing,
                    BlockReason = $"Active work claim for issue #{existing.IssueNumber}{FormatPullRequest(existing.PullRequestNumber)} is owned by another Codex session."
                };
            }

            if (existing is not null && !SameWork(existing, requested) && !CanEnrich(existing, requested))
            {
                return new WorkClaimAcquisitionResult
                {
                    Claim = existing,
                    BlockReason = $"Active work claim is already held for issue #{existing.IssueNumber}{FormatPullRequest(existing.PullRequestNumber)} and cannot be replaced by a different work identity."
                };
            }

            var now = DateTimeOffset.UtcNow;
            var claim = new WorkClaim
            {
                ClaimId = existing is not null && existing.ClaimId != Guid.Empty ? existing.ClaimId : Guid.NewGuid(),
                Version = (existing?.Version ?? 0) + 1,
                OwnerSessionId = requested.OwnerSessionId,
                IssueNumber = requested.IssueNumber,
                PullRequestNumber = requested.PullRequestNumber ?? existing?.PullRequestNumber,
                WorkType = existing?.WorkType ?? requested.WorkType,
                ClaimedIssueUpdatedAt = existing is not null && existing.ClaimedIssueUpdatedAt != default
                    ? existing.ClaimedIssueUpdatedAt
                    : requested.ClaimedIssueUpdatedAt,
                ClaimedAt = existing?.ClaimedAt ?? now,
                LastUpdatedAt = now
            };
            await WriteUnsafeAsync(gitCommonDirectory, claim, cancellationToken);
            return new WorkClaimAcquisitionResult { Acquired = true, Claim = claim };
        }, cancellationToken);

    public static Task<bool> ReleaseForIssueAsync(string gitCommonDirectory, int issueNumber, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var existing = await ReadUnsafeAsync(gitCommonDirectory, cancellationToken);
            if (existing?.IssueNumber != issueNumber) return false;
            DeleteUnsafe(gitCommonDirectory);
            return true;
        }, cancellationToken);

    public static Task<bool> ReleaseIfMatchesAsync(string gitCommonDirectory, WorkClaim expected, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var existing = await ReadUnsafeAsync(gitCommonDirectory, cancellationToken);
            if (existing is null || existing.ClaimId != expected.ClaimId || existing.Version != expected.Version) return false;
            DeleteUnsafe(gitCommonDirectory);
            return true;
        }, cancellationToken);

    public static Task<bool> ReleaseForPullRequestTransitionAsync(string gitCommonDirectory, WorkClaim expected, int pullRequestNumber, IReadOnlyCollection<int> closingIssueNumbers, bool isPassiveTarget, CancellationToken cancellationToken = default) =>
        ReleaseForPullRequestTransitionAsync(gitCommonDirectory, expected, pullRequestNumber, closingIssueNumbers, isPassiveTarget, false, cancellationToken);

    public static Task<bool> ReleaseForPullRequestTransitionAsync(string gitCommonDirectory, WorkClaim expected, int pullRequestNumber, IReadOnlyCollection<int> closingIssueNumbers, bool isPassiveTarget, bool isCurrentClaimPullRequest, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var existing = await ReadUnsafeAsync(gitCommonDirectory, cancellationToken);
            if (!isPassiveTarget || existing is null || existing.ClaimId != expected.ClaimId || existing.Version != expected.Version) return false;

            var matchesClaimedPullRequest = existing.PullRequestNumber == pullRequestNumber;
            var matchesInitialImplementation = existing.PullRequestNumber is null &&
                existing.WorkType == WorkClaimType.Implementation &&
                closingIssueNumbers.Contains(existing.IssueNumber) &&
                isCurrentClaimPullRequest;
            if (!matchesClaimedPullRequest && !matchesInitialImplementation) return false;

            DeleteUnsafe(gitCommonDirectory);
            return true;
        }, cancellationToken);

    private static async Task<WorkClaim?> ReadUnsafeAsync(string gitCommonDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(gitCommonDirectory, ClaimFileName);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkClaim>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteUnsafeAsync(string gitCommonDirectory, WorkClaim claim, CancellationToken cancellationToken)
    {
        var path = Path.Combine(gitCommonDirectory, ClaimFileName);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, claim, JsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, path, true);
    }

    private static void DeleteUnsafe(string gitCommonDirectory)
    {
        var path = Path.Combine(gitCommonDirectory, ClaimFileName);
        if (File.Exists(path)) File.Delete(path);
    }

    private static async Task<T> WithLockAsync<T>(string gitCommonDirectory, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(gitCommonDirectory);
        var lockPath = Path.Combine(gitCommonDirectory, LockFileName);
        FileStream? lockStream = null;
        for (var attempt = 0; attempt < 100 && lockStream is null; attempt++)
        {
            try { lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99) { await Task.Delay(25, cancellationToken); }
        }
        if (lockStream is null) throw new IOException("Could not acquire the repository work-claim lock.");
        await using (lockStream) return await action();
    }

    private static bool SameWork(WorkClaim left, WorkClaim right) =>
        left.IssueNumber == right.IssueNumber && left.PullRequestNumber == right.PullRequestNumber;

    private static bool CanEnrich(WorkClaim existing, WorkClaim requested) =>
        existing.IssueNumber == requested.IssueNumber &&
        existing.PullRequestNumber is null &&
        requested.PullRequestNumber.HasValue;

    private static string FormatPullRequest(int? pullRequestNumber) => pullRequestNumber.HasValue ? $" / pull request #{pullRequestNumber.Value}" : string.Empty;
}
