using System.Text.Json;

namespace CodexGithubRouter.Work;

public static class WorkClaimStore
{
    public const string ClaimFileName = "codex-github-router.work.json";

    /// <summary>
    /// Stable identity of the main worktree. The main worktree's git-dir is the Git common
    /// directory itself, which relocates with the repository, so ownership is keyed to this
    /// sentinel instead of an absolute path. It can never be classified stale: its resolved
    /// git-dir is the common directory, which exists whenever the claim file is readable.
    /// </summary>
    public const string MainWorktreeIdentity = ".";

    private const string LockFileName = "codex-github-router.work.lock";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Reads the active claim owned by the given worktree. Legacy single-claim files are
    /// migrated to the worktree-scoped format under the lock and the current claim is
    /// returned for the supplied worktree identity.
    /// </summary>
    public static Task<WorkClaim?> ReadAsync(string gitCommonDirectory, string worktreeId, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            return FindClaim(set, gitCommonDirectory, worktreeId);
        }, cancellationToken);

    public static async Task<WorkClaim?> TryReadAsync(string gitCommonDirectory, string worktreeId, CancellationToken cancellationToken = default)
    {
        var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: false, cancellationToken);
        return FindClaim(set, gitCommonDirectory, worktreeId);
    }

    /// <summary>
    /// Reads the complete repository-wide claim set so every worktree observes all active
    /// claims. Mutates the store only to persist a one-time legacy-format migration.
    /// </summary>
    public static Task<IReadOnlyList<WorkClaim>> ReadAllAsync(string gitCommonDirectory, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            return (IReadOnlyList<WorkClaim>)set.Claims;
        }, cancellationToken);

    public static async Task<IReadOnlyList<WorkClaim>> TryReadAllAsync(string gitCommonDirectory, CancellationToken cancellationToken = default)
    {
        var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: false, cancellationToken);
        return set.Claims;
    }

    public static Task<WorkClaimAcquisitionResult> TryAcquireAsync(string gitCommonDirectory, string worktreeId, WorkClaim requested, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var worktreeKey = NormalizeWorktreeId(gitCommonDirectory, worktreeId);
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            var claims = set.Claims;

            // A claim conflicts with another worktree's claim when the same issue is claimed
            // (regardless of pull-request identity) or when the same pull request is claimed under
            // a different issue number. Same-issue, PR-less claims and enriched claims therefore
            // occupy the same work across worktrees, while same-worktree continuation may still
            // enrich a PR-less claim with the single candidate pull request.
            var otherWorktreeClaim = claims.FirstOrDefault(candidate =>
                !ClaimOwnedByWorktree(candidate, gitCommonDirectory, worktreeKey) &&
                ConflictsWith(candidate, requested));
            if (otherWorktreeClaim is not null)
            {
                return new WorkClaimAcquisitionResult
                {
                    Claim = otherWorktreeClaim,
                    BlockReason = $"Active work claim for {FormatWorkIdentity(otherWorktreeClaim)} is owned by another Git worktree."
                };
            }

            var existing = claims.FirstOrDefault(candidate => ClaimOwnedByWorktree(candidate, gitCommonDirectory, worktreeKey));
            if (existing is not null && !string.Equals(existing.OwnerSessionId, requested.OwnerSessionId, StringComparison.Ordinal))
            {
                return new WorkClaimAcquisitionResult
                {
                    Claim = existing,
                    BlockReason = $"Active work claim for {FormatWorkIdentity(existing)} is owned by another Codex session."
                };
            }

            if (existing is not null && !SameWork(existing, requested) && !CanEnrich(existing, requested))
            {
                return new WorkClaimAcquisitionResult
                {
                    Claim = existing,
                    BlockReason = $"This worktree already owns an active work claim for {FormatWorkIdentity(existing)}; a worktree can hold only one active work item. Release it before claiming {FormatWorkIdentity(requested)}."
                };
            }

            var now = DateTimeOffset.UtcNow;
            var claim = new WorkClaim
            {
                ClaimId = existing is not null && existing.ClaimId != Guid.Empty ? existing.ClaimId : Guid.NewGuid(),
                Version = (existing?.Version ?? 0) + 1,
                WorktreeId = worktreeKey,
                WorktreePath = existing?.WorktreePath ?? Path.GetFullPath(worktreeId.Trim()),
                OwnerSessionId = requested.OwnerSessionId,
                IssueNumber = requested.IssueNumber,
                PullRequestNumber = requested.PullRequestNumber ?? existing?.PullRequestNumber,
                WorkType = existing?.WorkType ?? requested.WorkType,
                WorkerProfile = requested.WorkerProfile ?? existing?.WorkerProfile,
                Model = requested.Model ?? existing?.Model,
                ClaimedIssueUpdatedAt = existing is not null && existing.ClaimedIssueUpdatedAt != default
                    ? existing.ClaimedIssueUpdatedAt
                    : requested.ClaimedIssueUpdatedAt,
                ClaimedAt = existing?.ClaimedAt ?? now,
                LastUpdatedAt = now,
                ReviewerLogin = requested.ReviewerLogin ?? existing?.ReviewerLogin,
                ReviewCycleId = requested.ReviewCycleId ?? existing?.ReviewCycleId
            };

            if (existing is not null)
            {
                claims.Remove(existing);
            }

            claims.Add(claim);
            await WriteSetUnsafeAsync(gitCommonDirectory, set, cancellationToken);
            return new WorkClaimAcquisitionResult { Acquired = true, Claim = claim };
        }, cancellationToken);

    public static Task<bool> ReleaseForIssueAsync(string gitCommonDirectory, string worktreeId, int issueNumber, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            var claim = FindClaim(set, gitCommonDirectory, worktreeId);
            if (claim?.IssueNumber != issueNumber) return false;
            set.Claims.Remove(claim);
            await WriteSetUnsafeAsync(gitCommonDirectory, set, cancellationToken);
            return true;
        }, cancellationToken);

    public static Task<bool> ReleaseForPullRequestAsync(string gitCommonDirectory, string worktreeId, int pullRequestNumber, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            var claim = FindClaim(set, gitCommonDirectory, worktreeId);
            if (claim is null || claim.PullRequestNumber != pullRequestNumber) return false;
            set.Claims.Remove(claim);
            await WriteSetUnsafeAsync(gitCommonDirectory, set, cancellationToken);
            return true;
        }, cancellationToken);

    public static Task<bool> ReleaseIfMatchesAsync(string gitCommonDirectory, string worktreeId, WorkClaim expected, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            var claim = FindClaim(set, gitCommonDirectory, worktreeId);
            if (claim is null || claim.ClaimId != expected.ClaimId || claim.Version != expected.Version) return false;
            set.Claims.Remove(claim);
            await WriteSetUnsafeAsync(gitCommonDirectory, set, cancellationToken);
            return true;
        }, cancellationToken);

    public static Task<bool> ReleaseForPullRequestTransitionAsync(string gitCommonDirectory, string worktreeId, WorkClaim expected, int pullRequestNumber, IReadOnlyCollection<int> closingIssueNumbers, bool isPassiveTarget, CancellationToken cancellationToken = default) =>
        ReleaseForPullRequestTransitionAsync(gitCommonDirectory, worktreeId, expected, pullRequestNumber, closingIssueNumbers, isPassiveTarget, false, cancellationToken);

    public static Task<bool> ReleaseForPullRequestTransitionAsync(string gitCommonDirectory, string worktreeId, WorkClaim expected, int pullRequestNumber, IReadOnlyCollection<int> closingIssueNumbers, bool isPassiveTarget, bool isCurrentClaimPullRequest, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            var claim = FindClaim(set, gitCommonDirectory, worktreeId);
            if (!isPassiveTarget || claim is null || claim.ClaimId != expected.ClaimId || claim.Version != expected.Version) return false;

            var matchesClaimedPullRequest = claim.PullRequestNumber == pullRequestNumber;
            var matchesInitialImplementation = claim.PullRequestNumber is null &&
                claim.WorkType == WorkClaimType.Implementation &&
                claim.IssueNumber is { } claimedIssueNumber &&
                closingIssueNumbers.Contains(claimedIssueNumber) &&
                isCurrentClaimPullRequest;
            if (!matchesClaimedPullRequest && !matchesInitialImplementation) return false;

            set.Claims.Remove(claim);
            await WriteSetUnsafeAsync(gitCommonDirectory, set, cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>
    /// Removes claims owned by worktrees that no longer exist, applying the shared
    /// <see cref="IsStaleWorktree"/> evaluation. The caller supplies the existence predicate (for
    /// example <c>Directory.Exists</c>), so the store stays free of filesystem assumptions.
    /// Production uses this mutating path; read-only diagnostics use
    /// <see cref="TryReadActiveClaimsAsync"/> to exclude the same claims without writing.
    /// Returns the number of claims pruned.
    /// </summary>
    public static Task<int> PruneStaleWorktreesAsync(string gitCommonDirectory, Func<string, bool> worktreeExists, CancellationToken cancellationToken = default) =>
        WithLockAsync(gitCommonDirectory, async () =>
        {
            var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: true, cancellationToken);
            var before = set.Claims.Count;
            set.Claims.RemoveAll(claim => IsStaleWorktree(gitCommonDirectory, claim, worktreeExists));
            var removed = before - set.Claims.Count;
            if (removed > 0)
            {
                await WriteSetUnsafeAsync(gitCommonDirectory, set, cancellationToken);
            }

            return removed;
        }, cancellationToken);

    /// <summary>
    /// The single stale-worktree evaluation shared by production pruning (which mutates) and
    /// read-only diagnostics (which exclude without writing). A claim's worktree is stale when its
    /// resolved git-dir no longer exists. The main worktree resolves to the Git common directory
    /// itself, so its stable sentinel identity can never be classified stale, even after the
    /// repository directory is relocated.
    /// </summary>
    public static bool IsStaleWorktree(string gitCommonDirectory, WorkClaim claim, Func<string, bool>? worktreeExists = null)
    {
        worktreeExists ??= Directory.Exists;
        return !worktreeExists(ResolveWorktreePath(gitCommonDirectory, claim));
    }

    /// <summary>
    /// Non-mutating repository-wide read of claims whose worktree still exists, applying the same
    /// stale-worktree evaluation production pruning uses. Read-only diagnostics (explain/list) use
    /// this so a deleted worktree's claim is excluded exactly as the hook would prune it, without
    /// writing to the claim file.
    /// </summary>
    public static async Task<IReadOnlyList<WorkClaim>> TryReadActiveClaimsAsync(string gitCommonDirectory, CancellationToken cancellationToken = default)
    {
        var set = await ReadSetUnsafeAsync(gitCommonDirectory, persistLegacyMigration: false, cancellationToken);
        return set.Claims.Where(claim => !IsStaleWorktree(gitCommonDirectory, claim)).ToList();
    }

    private static WorkClaim? FindClaim(WorkClaimSet set, string gitCommonDirectory, string worktreeId)
    {
        var worktreeKey = NormalizeWorktreeId(gitCommonDirectory, worktreeId);
        return set.Claims.FirstOrDefault(claim => ClaimOwnedByWorktree(claim, gitCommonDirectory, worktreeKey));
    }

    private static bool ClaimOwnedByWorktree(WorkClaim claim, string gitCommonDirectory, string worktreeKey)
    {
        var storedKey = claim.WorktreeId.Trim();
        if (Path.IsPathRooted(storedKey))
        {
            // Legacy absolute identity: resolve it against the current common directory so an
            // existing claim file keeps matching after the identity scheme changed.
            storedKey = NormalizeWorktreeId(gitCommonDirectory, storedKey);
        }

        return string.Equals(NormalizeStablePath(storedKey), worktreeKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a claim's stable worktree identity to the absolute git-dir used for existence
    /// checks. The main worktree's sentinel resolves to the common directory itself; linked
    /// worktrees resolve relative to the common directory; legacy absolute identities are used
    /// verbatim.
    /// </summary>
    private static string ResolveWorktreePath(string gitCommonDirectory, WorkClaim claim)
    {
        var stored = claim.WorktreeId.Trim();
        var common = Path.GetFullPath(gitCommonDirectory.Trim());
        if (stored == MainWorktreeIdentity)
        {
            return common;
        }

        return Path.IsPathRooted(stored)
            ? Path.GetFullPath(stored)
            : Path.GetFullPath(Path.Combine(common, stored));
    }

    private static WorkClaimSet ReadSetUnsafeRaw(string gitCommonDirectory, string content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new WorkClaimFileException("The repository work-claim file is not valid claim JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null || root.ValueKind == JsonValueKind.Undefined)
            {
                throw new WorkClaimFileException("The work-claim file contains an invalid claim.");
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Claims", out _))
            {
                var set = JsonSerializer.Deserialize<WorkClaimSet>(content, JsonOptions);
                if (set is null || set.Claims is null)
                {
                    throw new WorkClaimFileException("The work-claim file contains an invalid claim set.");
                }

                foreach (var claim in set.Claims)
                {
                    ValidateClaim(claim);
                }

                return set;
            }

            var legacyClaim = JsonSerializer.Deserialize<WorkClaim>(content, JsonOptions);
            if (legacyClaim is null)
            {
                throw new WorkClaimFileException("The work-claim file contains an invalid claim.");
            }

            var mainWorktreeId = NormalizeWorktreeId(gitCommonDirectory, gitCommonDirectory);
            var migratedClaim = WithWorktree(legacyClaim, gitCommonDirectory, mainWorktreeId);
            ValidateClaim(migratedClaim);

            return new WorkClaimSet
            {
                Claims = new List<WorkClaim>
                {
                    migratedClaim
                }
            };
        }
    }

    private static async Task<WorkClaimSet> ReadSetUnsafeAsync(string gitCommonDirectory, bool persistLegacyMigration, CancellationToken cancellationToken)
    {
        var path = Path.Combine(gitCommonDirectory, ClaimFileName);
        if (!File.Exists(path)) return new WorkClaimSet();
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) throw new WorkClaimFileException("The work-claim file is empty or contains no claim set.");
        var set = ReadSetUnsafeRaw(gitCommonDirectory, content);

        if (persistLegacyMigration && !IsSetFormat(content))
        {
            await WriteSetUnsafeAsync(gitCommonDirectory, set, cancellationToken);
        }

        return set;
    }

    private static bool IsSetFormat(string content)
    {
        using var document = JsonDocument.Parse(content);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Claims", out _);
    }

    private static void ValidateClaim(WorkClaim? claim)
    {
        if (claim is null || claim.ClaimId == Guid.Empty || claim.Version <= 0 ||
            string.IsNullOrWhiteSpace(claim.WorktreeId) || string.IsNullOrWhiteSpace(claim.OwnerSessionId) ||
            !Enum.IsDefined(claim.WorkType) ||
            claim.ClaimedAt == default || claim.LastUpdatedAt == default)
        {
            throw new WorkClaimFileException("The work-claim file contains an invalid claim.");
        }

        switch (claim.WorkType)
        {
            case WorkClaimType.Implementation:
                if (claim.IssueNumber is not > 0)
                {
                    throw new WorkClaimFileException("The work-claim file contains an invalid claim: an implementation claim requires an issue number.");
                }

                break;
            case WorkClaimType.ChangeRequest:
                if (claim.IssueNumber is not > 0 || !claim.PullRequestNumber.HasValue)
                {
                    throw new WorkClaimFileException("The work-claim file contains an invalid claim: a change-request claim requires an issue number and a pull request number.");
                }

                break;
            case WorkClaimType.Review:
                if (!claim.PullRequestNumber.HasValue || string.IsNullOrWhiteSpace(claim.ReviewerLogin))
                {
                    throw new WorkClaimFileException("The work-claim file contains an invalid claim: a review claim requires a pull request number and a reviewer login.");
                }

                break;
        }
    }

    private static async Task WriteSetUnsafeAsync(string gitCommonDirectory, WorkClaimSet set, CancellationToken cancellationToken)
    {
        var path = Path.Combine(gitCommonDirectory, ClaimFileName);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, set, JsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, path, true);
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

    /// <summary>
    /// Normalizes a worktree identifier into its relocation-safe stored form. Non-rooted
    /// identities (the main-worktree sentinel and common-relative linked identities) are returned
    /// untouched; rooted identities are resolved relative to the Git common directory, so the main
    /// worktree becomes <see cref="MainWorktreeIdentity"/> and a linked worktree keeps its
    /// common-relative path regardless of where the repository directory lives.
    /// </summary>
    internal static string NormalizeWorktreeId(string gitCommonDirectory, string worktreeId)
    {
        var stored = NormalizeStablePath(worktreeId);
        if (!Path.IsPathRooted(stored))
        {
            return stored;
        }

        var common = Path.GetFullPath(gitCommonDirectory.Trim());
        return NormalizeStablePath(Path.GetRelativePath(common, Path.GetFullPath(stored)));
    }

    private static string NormalizeStablePath(string worktreeId)
    {
        var trimmed = worktreeId.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? worktreeId.Trim() : trimmed;
    }

    private static WorkClaim WithWorktree(WorkClaim claim, string gitCommonDirectory, string worktreeId) => new()
    {
        ClaimId = claim.ClaimId,
        Version = claim.Version,
        WorktreeId = worktreeId,
        WorktreePath = Path.GetFullPath(gitCommonDirectory),
        OwnerSessionId = claim.OwnerSessionId,
        IssueNumber = claim.IssueNumber,
        PullRequestNumber = claim.PullRequestNumber,
        WorkType = claim.WorkType,
        WorkerProfile = claim.WorkerProfile,
        Model = claim.Model,
        ClaimedIssueUpdatedAt = claim.ClaimedIssueUpdatedAt,
        ClaimedAt = claim.ClaimedAt,
        LastUpdatedAt = claim.LastUpdatedAt,
        ReviewerLogin = claim.ReviewerLogin,
        ReviewCycleId = claim.ReviewCycleId
    };

    private static bool SameWork(WorkClaim left, WorkClaim right)
    {
        if (left.WorkType == WorkClaimType.Review || right.WorkType == WorkClaimType.Review)
        {
            return left.WorkType == right.WorkType &&
                left.PullRequestNumber == right.PullRequestNumber &&
                string.Equals(left.ReviewerLogin, right.ReviewerLogin, StringComparison.OrdinalIgnoreCase);
        }

        return left.IssueNumber == right.IssueNumber && left.PullRequestNumber == right.PullRequestNumber;
    }

    /// <summary>
    /// True when the two claims occupy the same work item across the repository. Implementation and
    /// change-request claims conflict on the same issue (regardless of pull-request identity) or
    /// the same pull request under differing issue numbers. Review claims are PR-native and
    /// reviewer-scoped: they never conflict with implementation/change-request work and conflict
    /// with other review claims only on the same (pull request, reviewer) pair, so several
    /// reviewers may be requested for the same pull request independently.
    /// </summary>
    internal static bool ConflictsWith(WorkClaim left, WorkClaim right)
    {
        var leftIsReview = left.WorkType == WorkClaimType.Review;
        var rightIsReview = right.WorkType == WorkClaimType.Review;
        if (leftIsReview || rightIsReview)
        {
            return leftIsReview && rightIsReview &&
                left.PullRequestNumber.HasValue && right.PullRequestNumber.HasValue &&
                left.PullRequestNumber.Value == right.PullRequestNumber.Value &&
                string.Equals(left.ReviewerLogin, right.ReviewerLogin, StringComparison.OrdinalIgnoreCase);
        }

        return left.IssueNumber == right.IssueNumber ||
            (left.PullRequestNumber.HasValue && right.PullRequestNumber.HasValue && left.PullRequestNumber.Value == right.PullRequestNumber.Value);
    }

    private static bool CanEnrich(WorkClaim existing, WorkClaim requested) =>
        existing.WorkType != WorkClaimType.Review &&
        requested.WorkType != WorkClaimType.Review &&
        existing.IssueNumber == requested.IssueNumber &&
        existing.PullRequestNumber is null &&
        requested.PullRequestNumber.HasValue;

    private static string FormatWorkIdentity(WorkClaim claim) =>
        claim.WorkType == WorkClaimType.Review
            ? $"review of pull request #{claim.PullRequestNumber} (reviewer '{claim.ReviewerLogin}')"
            : $"issue #{claim.IssueNumber}{FormatPullRequest(claim.PullRequestNumber)}";

    private static string FormatPullRequest(int? pullRequestNumber) => pullRequestNumber.HasValue ? $" / pull request #{pullRequestNumber.Value}" : string.Empty;
}